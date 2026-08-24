using GridCore.Contracts.Events;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Customers;

/// <summary>What a caller supplies to register a customer.</summary>
/// <param name="Name">Who they are.</param>
/// <param name="Class">Residential or commercial.</param>
/// <param name="ContactName">Who to ask for.</param>
/// <param name="Email">Where to email them.</param>
/// <param name="Phone">Where to call them.</param>
/// <param name="DepositHeld">Security deposit taken, if any.</param>
public sealed record RegisterCustomerInput(
    string Name,
    CustomerClass Class,
    string? ContactName = null,
    string? Email = null,
    string? Phone = null,
    decimal DepositHeld = 0m);

/// <summary>What a caller supplies to correct a customer's details.</summary>
/// <param name="Name">Who they are.</param>
/// <param name="Class">Residential or commercial.</param>
/// <param name="ContactName">Who to ask for.</param>
/// <param name="Email">Where to email them.</param>
/// <param name="Phone">Where to call them.</param>
/// <param name="DepositHeld">Security deposit held.</param>
public sealed record UpdateCustomerInput(
    string Name,
    CustomerClass Class,
    string? ContactName = null,
    string? Email = null,
    string? Phone = null,
    decimal DepositHeld = 0m);

/// <summary>How the registry list is filtered.</summary>
/// <param name="Search">Matched against the account number and the name, case-insensitively.</param>
/// <param name="Status">Only customers in this status.</param>
/// <param name="Class">Only customers of this class.</param>
/// <param name="Limit">Most rows to return.</param>
public sealed record CustomerQuery(
    string? Search = null,
    CustomerStatus? Status = null,
    CustomerClass? Class = null,
    int Limit = 50);

/// <summary>The customer registry. The module's own surface; endpoints are a thin layer over it.</summary>
public interface ICustomerService
{
    /// <summary>Registers a customer, issuing them the next account number.</summary>
    Task<Customer> RegisterAsync(RegisterCustomerInput input, CancellationToken cancellationToken = default);

    /// <summary>Corrects a customer's details.</summary>
    Task<Customer> UpdateAsync(Guid id, UpdateCustomerInput input, CancellationToken cancellationToken = default);

    /// <summary>Moves a customer to another status.</summary>
    Task<Customer> ChangeStatusAsync(Guid id, CustomerStatus status, string? reason, CancellationToken cancellationToken = default);

    /// <summary>One customer, or <see langword="null"/> if there is no such id.</summary>
    Task<Customer?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The registry list, newest first.</summary>
    Task<IReadOnlyList<Customer>> ListAsync(CustomerQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// The customer registry over the customers schema.
/// </summary>
/// <remarks>
/// Every write runs inside <see cref="IUnitOfWork.ExecuteAsync"/> and never calls
/// <c>SaveChanges</c> itself. That is what puts the customer row (customers schema), its audit
/// entry and its outbox row (platform schema) in one transaction — invariants 1 and 2. A caller
/// that reads back a customer therefore knows its event is on its way, and a rollback takes the
/// event with it.
/// </remarks>
public sealed class CustomerService(
    CustomersDbContext database,
    IRegistryNumberGenerator numbers,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    IEventPublisher events,
    TimeProvider clock) : ICustomerService
{
    /// <summary>The largest page <see cref="ListAsync"/> will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public Task<Customer> RegisterAsync(RegisterCustomerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();
                var accountNumber = await numbers.NextCustomerAccountNumberAsync(ct).ConfigureAwait(false);

                // The unique index is the real guarantee; this check is what turns the loser of a
                // race into a 409 the caller can retry rather than a 500 out of the database.
                if (await database.Customers.AnyAsync(existing => existing.AccountNumber == accountNumber, ct).ConfigureAwait(false))
                {
                    throw new RegistryWorkflowException(
                        $"Account number {accountNumber} has just been taken by another registration. Try again.");
                }

                var customer = Customer.Register(
                    accountNumber,
                    input.Name,
                    input.Class,
                    now,
                    input.ContactName,
                    input.Email,
                    input.Phone,
                    input.DepositHeld);

                database.Customers.Add(customer);

                audit.Record(
                    AuditActions.CustomerCreated,
                    AuditEntityTypes.Customer,
                    customer.Id.ToString(),
                    before: null,
                    after: CustomerSnapshot.Of(customer));

                await events.PublishAsync(
                    CustomerRegistered.For(
                        now,
                        customer.Id,
                        customer.AccountNumber,
                        customer.Name,
                        customer.Class.ToString(),
                        customer.Status.ToString()),
                    ct).ConfigureAwait(false);

                return customer;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Customer> UpdateAsync(Guid id, UpdateCustomerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return MutateAsync(
            id,
            AuditActions.CustomerUpdated,
            customer => customer.UpdateDetails(input.Name, input.Class, input.ContactName, input.Email, input.Phone, input.DepositHeld),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Customer> ChangeStatusAsync(Guid id, CustomerStatus status, string? reason, CancellationToken cancellationToken = default) =>
        MutateAsync(
            id,
            AuditActions.CustomerStatusChanged,
            customer => customer.ChangeStatus(status, reason, clock.GetUtcNow()),
            cancellationToken);

    /// <inheritdoc />
    public Task<Customer?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Customers.FirstOrDefaultAsync(customer => customer.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Customer>> ListAsync(CustomerQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var customers = database.Customers.AsNoTracking();

        // Matched against non-nullable locals: both columns are stored by name, and EF cannot
        // translate a nullable-to-converted-value comparison.
        if (query.Status is { } status)
        {
            customers = customers.Where(customer => customer.Status == status);
        }

        if (query.Class is { } customerClass)
        {
            customers = customers.Where(customer => customer.Class == customerClass);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Lower-cased on both sides rather than ILIKE: Postgres's LIKE is case-sensitive and
            // ILIKE is Npgsql-only, which would leave the fast tier testing different SQL.
            var term = query.Search.Trim().ToLowerInvariant();

            customers = customers.Where(customer =>
                customer.AccountNumber.ToLower().Contains(term) || customer.Name.ToLower().Contains(term));
        }

        // Ordered by key, not by RegisteredAt: ids are Guid v7, so the primary-key index already
        // orders chronologically on Postgres and on the fast tier's SQLite alike.
        return await customers
            .OrderByDescending(customer => customer.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<Customer> MutateAsync(Guid id, string action, Action<Customer> mutate, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                var customer = await database.Customers.FirstOrDefaultAsync(candidate => candidate.Id == id, ct).ConfigureAwait(false)
                    ?? throw new CustomerNotFoundException(id);

                var before = CustomerSnapshot.Of(customer);

                mutate(customer);

                audit.Record(action, AuditEntityTypes.Customer, customer.Id.ToString(), before, CustomerSnapshot.Of(customer));

                return customer;
            },
            cancellationToken);
}

/// <summary>
/// The before/after shape a customer is audited as. A dedicated record rather than the entity, so
/// changing the entity later cannot silently change the meaning of historic audit entries.
/// </summary>
/// <param name="Id">Which customer.</param>
/// <param name="AccountNumber">Their account number.</param>
/// <param name="Name">Who they are.</param>
/// <param name="ContactName">Who to ask for.</param>
/// <param name="Email">Where to email them.</param>
/// <param name="Phone">Where to call them.</param>
/// <param name="Class">Residential or commercial.</param>
/// <param name="Status">Where they stand.</param>
/// <param name="DepositHeld">Deposit held at the time of the snapshot.</param>
public sealed record CustomerSnapshot(
    Guid Id,
    string AccountNumber,
    string Name,
    string? ContactName,
    string? Email,
    string? Phone,
    CustomerClass Class,
    CustomerStatus Status,
    decimal DepositHeld)
{
    /// <summary>Takes a snapshot of <paramref name="customer"/> as it stands.</summary>
    public static CustomerSnapshot Of(Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);

        return new CustomerSnapshot(
            customer.Id,
            customer.AccountNumber,
            customer.Name,
            customer.ContactName,
            customer.Email,
            customer.Phone,
            customer.Class,
            customer.Status,
            customer.DepositHeld);
    }
}
