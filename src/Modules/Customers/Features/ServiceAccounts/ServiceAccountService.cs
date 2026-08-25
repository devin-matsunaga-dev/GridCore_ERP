using GridCore.Contracts.Events;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.ServiceAccounts;

/// <summary>What a caller supplies to open a service account.</summary>
/// <param name="CustomerId">Who is to be served.</param>
/// <param name="ServiceLocationId">Where they are to be served.</param>
/// <param name="Reason">Why the account is being opened, for the history.</param>
public sealed record OpenServiceAccountInput(Guid CustomerId, Guid ServiceLocationId, string? Reason = null);

/// <summary>How the service account list is filtered.</summary>
/// <param name="Search">Matched against the account number, case-insensitively.</param>
/// <param name="CustomerId">Only accounts held by this customer — the customer 360 query.</param>
/// <param name="ServiceLocationId">Only accounts at this premise.</param>
/// <param name="Status">Only accounts in this status.</param>
/// <param name="Limit">Most rows to return.</param>
public sealed record ServiceAccountQuery(
    string? Search = null,
    Guid? CustomerId = null,
    Guid? ServiceLocationId = null,
    ServiceAccountStatus? Status = null,
    int Limit = 50);

/// <summary>The service account registry and its lifecycle. Endpoints are a thin layer over it.</summary>
public interface IServiceAccountService
{
    /// <summary>Opens an account joining a customer to a premise, issuing the next account number.</summary>
    Task<ServiceAccount> OpenAsync(OpenServiceAccountInput input, CancellationToken cancellationToken = default);

    /// <summary>Energises supply on an account — the first connection or a reconnection.</summary>
    Task<ServiceAccount> StartServiceAsync(Guid id, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Cuts supply, leaving the account open.</summary>
    Task<ServiceAccount> StopServiceAsync(Guid id, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Closes an account for good, releasing its premise.</summary>
    Task<ServiceAccount> CloseAsync(Guid id, string? reason, CancellationToken cancellationToken = default);

    /// <summary>One account with its history, or <see langword="null"/> if there is no such id.</summary>
    Task<ServiceAccount?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The account list, newest first.</summary>
    Task<IReadOnlyList<ServiceAccount>> ListAsync(ServiceAccountQuery query, CancellationToken cancellationToken = default);

    /// <summary>One account's service history, oldest first.</summary>
    /// <exception cref="ServiceAccountNotFoundException">There is no account with that id.</exception>
    Task<IReadOnlyList<ServiceAccountHistoryEntry>> HistoryAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// The service account registry over the customers schema.
/// </summary>
/// <remarks>
/// Every write runs inside <see cref="IUnitOfWork.ExecuteAsync"/> and never calls
/// <c>SaveChanges</c> itself, so the account row, its history line, its audit entry and its outbox
/// row are one transaction — invariants 1 and 2. The history line is written by the aggregate
/// rather than here, which is what makes "the status moved but nothing recorded why" impossible.
/// </remarks>
public sealed class ServiceAccountService(
    CustomersDbContext database,
    IRegistryNumberGenerator numbers,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    IEventPublisher events,
    ICurrentUser currentUser,
    TimeProvider clock) : IServiceAccountService
{
    /// <summary>The largest page <see cref="ListAsync"/> will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// The customer statuses an account may be opened against. A suspended customer is barred from
    /// new service by definition, and a closed one has left — neither is a validation failure, both
    /// are a 409 that says what is in the way.
    /// </summary>
    public static IReadOnlyList<CustomerStatus> OpenableCustomerStatuses { get; } =
        [CustomerStatus.Prospect, CustomerStatus.Active];

    /// <inheritdoc />
    public Task<ServiceAccount> OpenAsync(OpenServiceAccountInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                // FindAsync, not a query: a lookup by primary key checks the change tracker before
                // it touches the database, and the intake wizard (WP-2.8) opens an account against a
                // customer and a premise added moments earlier in this same transaction — neither of
                // which any SQL query can see until it commits. A query here would answer "no such
                // customer" for a customer that is right there in the context.
                var customer = await database.Customers.FindAsync([input.CustomerId], ct).ConfigureAwait(false)
                    ?? throw new CustomerNotFoundException(input.CustomerId);

                var location = await database.ServiceLocations.FindAsync([input.ServiceLocationId], ct).ConfigureAwait(false)
                    ?? throw new ServiceLocationNotFoundException(input.ServiceLocationId);

                if (!OpenableCustomerStatuses.Contains(customer.Status))
                {
                    throw new RegistryWorkflowException(
                        $"Customer {customer.AccountNumber} is {customer.Status} and cannot take on new service.");
                }

                if (!location.IsActive)
                {
                    throw new RegistryWorkflowException(
                        $"Service location {location.LocationCode} is deactivated and cannot be connected.");
                }

                // Opening the customer's status is not this call's to do. A prospect becoming a
                // customer is somebody's decision, and a cross-aggregate side effect here would
                // move a status nobody asked to move — WP-2.x can consume ServiceStarted for that.
                var openAtPremise = await database.ServiceAccounts
                    .Where(existing => existing.ServiceLocationId == input.ServiceLocationId)
                    .Where(existing => existing.Status != ServiceAccountStatus.Closed)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);

                if (openAtPremise is not null)
                {
                    throw new RegistryWorkflowException(
                        $"Service location {location.LocationCode} is already served by account {openAtPremise.AccountNumber} "
                        + $"({openAtPremise.Status}). Close that account before opening another there.");
                }

                var accountNumber = await numbers.NextServiceAccountNumberAsync(ct).ConfigureAwait(false);

                // The unique index is the real guarantee; this turns the loser of a race into a 409
                // the caller can retry rather than a 500 out of the database.
                if (await database.ServiceAccounts.AnyAsync(existing => existing.AccountNumber == accountNumber, ct).ConfigureAwait(false))
                {
                    throw new RegistryWorkflowException(
                        $"Account number {accountNumber} has just been taken by another registration. Try again.");
                }

                var account = ServiceAccount.Open(
                    accountNumber,
                    input.CustomerId,
                    input.ServiceLocationId,
                    RegistryActor.Of(currentUser),
                    now,
                    input.Reason);

                database.ServiceAccounts.Add(account);

                audit.Record(
                    AuditActions.ServiceAccountOpened,
                    AuditEntityTypes.ServiceAccount,
                    account.Id.ToString(),
                    before: null,
                    after: ServiceAccountSnapshot.Of(account));

                await events.PublishAsync(
                    ServiceAccountOpened.For(
                        now,
                        account.Id,
                        account.AccountNumber,
                        account.CustomerId,
                        account.ServiceLocationId,
                        account.Status.ToString()),
                    ct).ConfigureAwait(false);

                return account;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ServiceAccount> StartServiceAsync(Guid id, string? reason, CancellationToken cancellationToken = default) =>
        TransitionAsync(
            id,
            AuditActions.ServiceAccountStarted,
            (account, actor, now) => account.StartService(actor, now, reason),
            (account, now) => ServiceStarted.For(now, account.Id, account.AccountNumber, account.CustomerId, account.ServiceLocationId, reason),
            cancellationToken);

    /// <inheritdoc />
    public Task<ServiceAccount> StopServiceAsync(Guid id, string? reason, CancellationToken cancellationToken = default) =>
        TransitionAsync(
            id,
            AuditActions.ServiceAccountStopped,
            (account, actor, now) => account.StopService(actor, now, reason),
            (account, now) => ServiceStopped.For(now, account.Id, account.AccountNumber, account.CustomerId, account.ServiceLocationId, reason),
            cancellationToken);

    /// <inheritdoc />
    public Task<ServiceAccount> CloseAsync(Guid id, string? reason, CancellationToken cancellationToken = default) =>
        TransitionAsync(
            id,
            AuditActions.ServiceAccountClosed,
            (account, actor, now) => account.Close(actor, now, reason),
            (account, now) => ServiceAccountClosed.For(now, account.Id, account.AccountNumber, account.CustomerId, account.ServiceLocationId, reason),
            cancellationToken);

    /// <inheritdoc />
    public Task<ServiceAccount?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.ServiceAccounts
            .Include(account => account.History)
            .FirstOrDefaultAsync(account => account.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ServiceAccount>> ListAsync(ServiceAccountQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No Include: a list row shows where an account stands, not how it got there. The history
        // is one more request away, on the account that is actually being looked at.
        var accounts = database.ServiceAccounts.AsNoTracking();

        if (query.CustomerId is { } customerId)
        {
            accounts = accounts.Where(account => account.CustomerId == customerId);
        }

        if (query.ServiceLocationId is { } locationId)
        {
            accounts = accounts.Where(account => account.ServiceLocationId == locationId);
        }

        // Matched against a non-nullable local: the column is stored by name, and EF cannot
        // translate a nullable-to-converted-value comparison.
        if (query.Status is { } status)
        {
            accounts = accounts.Where(account => account.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Lower-cased on both sides rather than ILIKE, so the fast tier exercises the same SQL
            // shape production runs.
            var term = query.Search.Trim().ToLowerInvariant();

            accounts = accounts.Where(account => account.AccountNumber.ToLower().Contains(term));
        }

        // Ordered by key: ids are Guid v7, so the primary-key index already orders chronologically
        // on Postgres and on the fast tier's SQLite alike.
        return await accounts
            .OrderByDescending(account => account.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ServiceAccountHistoryEntry>> HistoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await database.ServiceAccounts.AnyAsync(account => account.Id == id, cancellationToken).ConfigureAwait(false))
        {
            // Distinguished from an account that simply has no lines, which cannot happen — every
            // account is opened with one — but an empty list for a missing id would say it had.
            throw new ServiceAccountNotFoundException(id);
        }

        return await database.ServiceAccountHistory
            .AsNoTracking()
            .Where(entry => entry.ServiceAccountId == id)
            .OrderBy(entry => entry.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<ServiceAccount> TransitionAsync(
        Guid id,
        string action,
        Action<ServiceAccount, RegistryActor, DateTimeOffset> transition,
        Func<ServiceAccount, DateTimeOffset, IIntegrationEvent> describe,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                // The change tracker first, for the same reason OpenAsync uses FindAsync: an account
                // opened moments earlier in this transaction (the intake wizard, WP-2.8, energising
                // supply as part of the intake) is not visible to any query until it commits, and it
                // is already carrying the opening history line the Include would have fetched.
                var account = database.ServiceAccounts.Local.FirstOrDefault(candidate => candidate.Id == id)
                    ?? await database.ServiceAccounts
                        .Include(candidate => candidate.History)
                        .FirstOrDefaultAsync(candidate => candidate.Id == id, ct).ConfigureAwait(false)
                    ?? throw new ServiceAccountNotFoundException(id);

                var before = ServiceAccountSnapshot.Of(account);

                transition(account, RegistryActor.Of(currentUser), now);

                audit.Record(action, AuditEntityTypes.ServiceAccount, account.Id.ToString(), before, ServiceAccountSnapshot.Of(account));

                await events.PublishAsync(describe(account, now), ct).ConfigureAwait(false);

                return account;
            },
            cancellationToken);
}

/// <summary>
/// The before/after shape a service account is audited as. A dedicated record rather than the
/// entity, so changing the entity later cannot silently change the meaning of historic entries.
/// </summary>
/// <param name="Id">Which account.</param>
/// <param name="AccountNumber">Its number.</param>
/// <param name="CustomerId">Who is served.</param>
/// <param name="ServiceLocationId">Where.</param>
/// <param name="Status">Where the account stands.</param>
/// <param name="ServiceStartedAt">When supply was most recently energised.</param>
/// <param name="ServiceEndedAt">When supply was most recently cut.</param>
/// <param name="StatusReason">Why the status last moved.</param>
public sealed record ServiceAccountSnapshot(
    Guid Id,
    string AccountNumber,
    Guid CustomerId,
    Guid ServiceLocationId,
    ServiceAccountStatus Status,
    DateTimeOffset? ServiceStartedAt,
    DateTimeOffset? ServiceEndedAt,
    string? StatusReason)
{
    /// <summary>Takes a snapshot of <paramref name="account"/> as it stands.</summary>
    public static ServiceAccountSnapshot Of(ServiceAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return new ServiceAccountSnapshot(
            account.Id,
            account.AccountNumber,
            account.CustomerId,
            account.ServiceLocationId,
            account.Status,
            account.ServiceStartedAt,
            account.ServiceEndedAt,
            account.StatusReason);
    }
}
