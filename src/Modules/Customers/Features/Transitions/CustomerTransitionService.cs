using GridCore.Contracts.Directories;
using GridCore.Contracts.Events;
using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Transitions;

/// <summary>What a caller supplies to move a customer between classes.</summary>
/// <param name="Class">What they are to become.</param>
/// <param name="ReasonCode">Why, from the fixed list.</param>
/// <param name="EffectiveOn">The day the new class applies from. Today when the caller does not say.</param>
/// <param name="Notes">What the operator wants to add. Required with <see cref="TransitionReasonCode.Other"/>.</param>
public sealed record ChangeCustomerClassInput(
    CustomerClass Class,
    TransitionReasonCode ReasonCode,
    DateOnly? EffectiveOn = null,
    string? Notes = null);

/// <summary>What a caller supplies to move a customer between statuses.</summary>
/// <param name="Status">Where they should end up.</param>
/// <param name="ReasonCode">Why, from the fixed list.</param>
/// <param name="EffectiveOn">The day the new status applies from. Today when the caller does not say.</param>
/// <param name="Notes">What the operator wants to add. Required with <see cref="TransitionReasonCode.Other"/>.</param>
public sealed record ChangeCustomerStatusInput(
    CustomerStatus Status,
    TransitionReasonCode ReasonCode,
    DateOnly? EffectiveOn = null,
    string? Notes = null);

/// <summary>What a caller supplies to move a customer in at a premise they were not being served at.</summary>
/// <param name="ServiceLocationId">Where they are moving in.</param>
/// <param name="ReasonCode">Why, from the fixed list.</param>
/// <param name="ServiceType">Which supply they are taking up. Electricity when the caller does not say.</param>
/// <param name="EffectiveOn">The day service is taken up. Today when the caller does not say.</param>
/// <param name="Notes">What the operator wants to add. Required with <see cref="TransitionReasonCode.Other"/>.</param>
public sealed record MoveInInput(
    Guid ServiceLocationId,
    TransitionReasonCode ReasonCode,
    ServiceType ServiceType = ServiceType.Electricity,
    DateOnly? EffectiveOn = null,
    string? Notes = null);

/// <summary>What a caller supplies to end a customer's service at a premise.</summary>
/// <param name="ServiceAccountId">The account to close.</param>
/// <param name="ReasonCode">Why, from the fixed list.</param>
/// <param name="EffectiveOn">The day service ended — what a final bill is raised to. Today when the caller does not say.</param>
/// <param name="Notes">What the operator wants to add. Required with <see cref="TransitionReasonCode.Other"/>.</param>
public sealed record MoveOutInput(
    Guid ServiceAccountId,
    TransitionReasonCode ReasonCode,
    DateOnly? EffectiveOn = null,
    string? Notes = null);

/// <summary>What a caller supplies to move a customer's service from one premise to another.</summary>
/// <param name="FromServiceAccountId">The account to close at the premise being left.</param>
/// <param name="ToServiceLocationId">The premise being taken up.</param>
/// <param name="ReasonCode">Why, from the fixed list.</param>
/// <param name="EffectiveOn">The day service moved. Today when the caller does not say.</param>
/// <param name="Notes">What the operator wants to add. Required with <see cref="TransitionReasonCode.Other"/>.</param>
public sealed record TransferServiceInput(
    Guid FromServiceAccountId,
    Guid ToServiceLocationId,
    TransitionReasonCode ReasonCode,
    DateOnly? EffectiveOn = null,
    string? Notes = null);

/// <summary>How the transition register is filtered.</summary>
/// <param name="Kind">Only transitions of this kind.</param>
/// <param name="ServiceAccountId">Only transitions naming this account on either side — released or taken up.</param>
/// <param name="Limit">Most rows to return.</param>
public sealed record TransitionQuery(
    AccountTransitionKind? Kind = null,
    Guid? ServiceAccountId = null,
    int Limit = 100);

/// <summary>
/// The account transitions (WP-2.15): the two changes that alter what a customer is billed.
/// </summary>
public interface ICustomerTransitionService
{
    /// <summary>One customer's transition register, newest first.</summary>
    /// <exception cref="CustomerNotFoundException">There is no such customer.</exception>
    Task<IReadOnlyList<AccountTransition>> ListAsync(
        Guid customerId,
        TransitionQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Moves a customer between classes, from an effective date forward.</summary>
    Task<AccountTransition> ChangeClassAsync(Guid customerId, ChangeCustomerClassInput input, CancellationToken cancellationToken = default);

    /// <summary>Moves a customer between statuses, from an effective date forward.</summary>
    Task<AccountTransition> ChangeStatusAsync(Guid customerId, ChangeCustomerStatusInput input, CancellationToken cancellationToken = default);

    /// <summary>Opens an account for an existing customer at a premise they were not being served at.</summary>
    Task<AccountTransition> MoveInAsync(Guid customerId, MoveInInput input, CancellationToken cancellationToken = default);

    /// <summary>Closes an account, ending service at that premise.</summary>
    Task<AccountTransition> MoveOutAsync(Guid customerId, MoveOutInput input, CancellationToken cancellationToken = default);

    /// <summary>Moves a customer's service from one premise to another as one linked act.</summary>
    Task<AccountTransition> TransferAsync(Guid customerId, TransferServiceInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// The transition register over the customers schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>This service records; it does not decide what is legal.</b> WP-1.1's
/// <c>CustomerTransitions</c> and WP-1.2's <c>ServiceAccountTransitions</c> still own the state
/// machines, and the premise-occupancy rule still lives in
/// <see cref="ServiceAccountService.OpenAsync"/>. WORK_PACKAGES.md is explicit that this phase
/// "adds reasons and linkage, it does not loosen the transitions", so every move here goes through
/// the aggregate or the service that already owned it, and an illegal one is the same 409 it always
/// was.
/// </para>
/// <para>
/// <b>Every transition is permission-gated, audited and — where it changes what gets billed —
/// published.</b> Invariant 5 for the first two, invariant 2 for the third. The gate is
/// <see cref="Permissions.Customers.Transition"/>, demanded <i>here</i> as well as on the route: the
/// endpoints are entirely transition acts so they carry it too, but this service is reachable in
/// process — a later module moving somebody out to complete a disconnection work order will call
/// <see cref="MoveOutAsync"/> and not a URL — and CONVENTIONS.md's rule is that a service enforces
/// its own permissions rather than trusting whoever called it. One check here covers every caller.
/// </para>
/// <para>
/// <b>A transfer is one transaction.</b> The old account's closure, the new account's opening, the
/// deposit carry, the register row, three audit entries and the outbox messages all commit together
/// or not at all — which is what "one linked transfer" has to mean if it is to mean anything. The
/// nested <see cref="IUnitOfWork.ExecuteAsync"/> calls inside <see cref="IServiceAccountService"/>
/// join this one rather than opening their own, exactly as WP-2.8's intake composes three registries.
/// </para>
/// <para>
/// <b>The deposit is carried, not refunded and re-collected.</b> It is held against the customer and
/// both accounts are that customer's, so nothing moves: see
/// <see cref="DepositEntryKind.Transferred"/>, which has a direction of zero and exists so the carry
/// is readable rather than silent.
/// </para>
/// </remarks>
public sealed class CustomerTransitionService(
    CustomersDbContext database,
    IServiceAccountService accounts,
    IDepositRuleService rules,
    IBillDirectory bills,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    IEventPublisher events,
    ICurrentUser currentUser,
    TimeProvider clock) : ICustomerTransitionService
{
    /// <summary>The largest page <see cref="ListAsync"/> will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountTransition>> ListAsync(
        Guid customerId,
        TransitionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Distinguished from a customer who simply has no transitions, which is the ordinary state of
        // a customer nothing has happened to. An empty list for a missing id would say they had none.
        if (!await database.Customers.AnyAsync(candidate => candidate.Id == customerId, cancellationToken).ConfigureAwait(false))
        {
            throw new CustomerNotFoundException(customerId);
        }

        var transitions = database.AccountTransitions
            .AsNoTracking()
            .Where(transition => transition.CustomerId == customerId);

        // Matched against a non-nullable local: the column is stored by name, and EF cannot translate
        // a nullable-to-converted-value comparison.
        if (query.Kind is { } kind)
        {
            transitions = transitions.Where(transition => transition.Kind == kind);
        }

        if (query.ServiceAccountId is { } accountId)
        {
            // BOTH sides. An account appears as the one released on a move-out or a transfer, and as
            // the one taken up on a move-in or the other half of that same transfer — "what happened
            // to this account" wants all four, which is why the configuration indexes each column.
            transitions = transitions.Where(transition =>
                transition.FromServiceAccountId == accountId || transition.ToServiceAccountId == accountId);
        }

        // Ordered by key: ids are Guid v7, so the primary-key index already orders chronologically on
        // Postgres and on the fast tier's SQLite alike.
        return await transitions
            .OrderByDescending(transition => transition.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AccountTransition> ChangeClassAsync(Guid customerId, ChangeCustomerClassInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return RecordAsync(
            customerId,
            async (customer, actor, now, ct) =>
            {
                var effectiveOn = input.EffectiveOn ?? Today(now);
                var from = customer.Class;
                var before = AccountTransitionSnapshot.Of(customer, null);

                await RequireNoIssuedBillBehindAsync(customer, effectiveOn, ct).ConfigureAwait(false);

                customer.ChangeClass(input.Class, input.ReasonCode, effectiveOn, now);

                var transition = AccountTransition.ClassChanged(
                    customer,
                    from,
                    customer.Class,
                    input.ReasonCode,
                    input.Notes,
                    effectiveOn,
                    actor,
                    now);

                await events.PublishAsync(
                    CustomerClassChanged.For(
                        now,
                        transition.Id,
                        customer.Id,
                        customer.AccountNumber,
                        from.ToString(),
                        customer.Class.ToString(),
                        effectiveOn,
                        input.ReasonCode.ToString(),
                        transition.Notes),
                    ct).ConfigureAwait(false);

                return new TransitionOutcome(transition, AuditActions.CustomerClassChanged, before, Account: null);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<AccountTransition> ChangeStatusAsync(Guid customerId, ChangeCustomerStatusInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return RecordAsync(
            customerId,
            (customer, actor, now, _) =>
            {
                var effectiveOn = input.EffectiveOn ?? Today(now);
                var from = customer.Status;
                var before = AccountTransitionSnapshot.Of(customer, null);

                // NO bill guard here, unlike a class change, and the asymmetry is the point: a class
                // decides which tariff a bill is priced on, so dating one behind an issued bill would
                // say the utility had charged the wrong rate. A status decides whether the customer
                // may take on new service; back-dating a suspension re-prices nothing.
                customer.ChangeStatus(input.Status, input.ReasonCode, effectiveOn, input.Notes, now);

                var transition = AccountTransition.StatusChanged(
                    customer,
                    from,
                    customer.Status,
                    input.ReasonCode,
                    input.Notes,
                    effectiveOn,
                    actor,
                    now);

                // No event. Nothing downstream prices off a customer's status today, and publishing
                // one nobody consumes would be an instruction rather than a fact — the call WP-2.2
                // made about "cycle finished, go and bill it". WP-1.1's CustomerRegistered already
                // carries the status a customer starts on.
                return Task.FromResult(new TransitionOutcome(transition, AuditActions.CustomerStatusChanged, before, Account: null));
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<AccountTransition> MoveInAsync(Guid customerId, MoveInInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return RecordAsync(
            customerId,
            async (customer, actor, now, ct) =>
            {
                var effectiveOn = input.EffectiveOn ?? Today(now);

                // No account on the before snapshot, which is what a move-in IS: the customer was
                // not being served at this premise, so there is nothing there to have moved.
                var before = AccountTransitionSnapshot.Of(customer, null);

                // Straight through WP-1.2's own service, which is where the premise-occupancy rule,
                // the customer-status rule and the account number live — and which writes its own
                // audit entry and publishes ServiceAccountOpened. A move-in is that act with a reason
                // code and a date recorded beside it, not a second implementation of it.
                var opened = await accounts.OpenAsync(
                    new OpenServiceAccountInput(
                        customer.Id,
                        input.ServiceLocationId,
                        input.ServiceType,
                        Describe(input.ReasonCode, input.Notes)),
                    ct).ConfigureAwait(false);

                var transition = AccountTransition.MovedIn(
                    customer,
                    opened,
                    input.ReasonCode,
                    input.Notes,
                    effectiveOn,
                    actor,
                    now);

                // No event of its own: ServiceAccountOpened has already said a premise was taken up,
                // and a new account has no service period behind it to final-bill. A move-OUT is
                // different and does publish, because a final bill has to be cut to a date.
                return new TransitionOutcome(transition, AuditActions.ServiceMovedIn, before, opened);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<AccountTransition> MoveOutAsync(Guid customerId, MoveOutInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return RecordAsync(
            customerId,
            async (customer, actor, now, ct) =>
            {
                var effectiveOn = input.EffectiveOn ?? Today(now);
                var account = await RequireOwnAccountAsync(customer, input.ServiceAccountId, ct).ConfigureAwait(false);

                RequireServiceHadBegunBy(account, effectiveOn);

                // Taken while the account is still open, which is the whole value of a before: the
                // pair reads Active then Closed. CloseAsync mutates the same tracked instance, so a
                // snapshot taken afterwards would say it had always been closed.
                var before = AccountTransitionSnapshot.Of(customer, account);

                var locationId = account.ServiceLocationId;

                // WP-1.2's own transition, with its state machine, its history line, its audit entry
                // and its ServiceAccountClosed event. A move-out is that act, dated and coded.
                var closed = await accounts.CloseAsync(account.Id, Describe(input.ReasonCode, input.Notes), ct).ConfigureAwait(false);

                var transition = AccountTransition.MovedOut(
                    customer,
                    closed,
                    input.ReasonCode,
                    input.Notes,
                    effectiveOn,
                    actor,
                    now);

                await events.PublishAsync(
                    ServiceMovedOut.For(
                        now,
                        transition.Id,
                        customer.Id,
                        closed.Id,
                        closed.AccountNumber,
                        locationId,
                        effectiveOn,
                        input.ReasonCode.ToString(),
                        transition.Notes),
                    ct).ConfigureAwait(false);

                return new TransitionOutcome(transition, AuditActions.ServiceMovedOut, before, closed);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<AccountTransition> TransferAsync(Guid customerId, TransferServiceInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return RecordAsync(
            customerId,
            async (customer, actor, now, ct) =>
            {
                var effectiveOn = input.EffectiveOn ?? Today(now);
                var account = await RequireOwnAccountAsync(customer, input.FromServiceAccountId, ct).ConfigureAwait(false);

                RequireServiceHadBegunBy(account, effectiveOn);

                if (account.ServiceLocationId == input.ToServiceLocationId)
                {
                    throw new RegistryWorkflowException(
                        $"Account {account.AccountNumber} is already served at that premise. "
                        + "A transfer moves service between two premises; there is nothing here to move.");
                }

                // The premise being left, and the account as it stood while it still held it.
                var fromLocationId = account.ServiceLocationId;
                var before = AccountTransitionSnapshot.Of(customer, account);

                // Closed first, then opened — the order it happens in, and the order the two audit
                // entries read in. Which premise is free is not affected either way: the destination
                // is a different premise by the guard above, and OpenAsync refuses an occupied one
                // with the account number that is in the way.
                var closed = await accounts.CloseAsync(account.Id, Describe(input.ReasonCode, input.Notes), ct).ConfigureAwait(false);

                var opened = await accounts.OpenAsync(
                    new OpenServiceAccountInput(
                        customer.Id,
                        input.ToServiceLocationId,

                        // The service the customer was already taking, never a choice. A transfer
                        // moves one supply from one address to another; changing what is supplied on
                        // the way would be a move-out and a move-in wearing one reason code.
                        account.ServiceType,
                        Describe(input.ReasonCode, input.Notes)),
                    ct).ConfigureAwait(false);

                var (carried, currency, entry) = await CarryDepositAsync(customer, closed, opened, actor, now, ct).ConfigureAwait(false);

                var transition = AccountTransition.Transferred(
                    customer,
                    closed,
                    opened,
                    carried,
                    currency,
                    entry?.Id,
                    input.ReasonCode,
                    input.Notes,
                    effectiveOn,
                    actor,
                    now);

                await events.PublishAsync(
                    ServiceTransferred.For(
                        now,
                        transition.Id,
                        customer.Id,
                        closed.Id,
                        closed.AccountNumber,
                        fromLocationId,
                        opened.Id,
                        opened.AccountNumber,
                        input.ToServiceLocationId,
                        effectiveOn,
                        carried,
                        currency,
                        input.ReasonCode.ToString(),
                        transition.Notes),
                    ct).ConfigureAwait(false);

                return new TransitionOutcome(transition, AuditActions.ServiceTransferred, before, opened);
            },
            cancellationToken);
    }

    /// <summary>
    /// Carries whatever deposit the customer is holding across a transfer, and says how much that was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No entry when nothing is held.</b> <see cref="DepositEntry.Carry"/> refuses a zero amount,
    /// and rightly — an entry saying nothing was carried is a row nobody can reconcile, the same
    /// argument <see cref="DepositEntryKind"/> makes for having no <c>Held</c> member. A customer who
    /// has never paid a deposit transfers perfectly well; the register records a carry of zero and
    /// the ledger is untouched.
    /// </para>
    /// <para>
    /// <b>The currency comes from the most recent collection</b> — the terms the money now held was
    /// actually taken under — falling back to the schedule for a customer who has none. That is the
    /// rule <c>CustomerDepositService.GetAsync</c> states for the ledger a rep reads, and the two
    /// must not disagree about what a balance is denominated in.
    /// </para>
    /// </remarks>
    private async Task<(decimal Carried, string Currency, DepositEntry? Entry)> CarryDepositAsync(
        Customer customer,
        ServiceAccount closed,
        ServiceAccount opened,
        RegistryActor actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var currency = await database.DepositEntries
            .Where(entry => entry.CustomerId == customer.Id)
            .Where(entry => entry.Kind == DepositEntryKind.Collected)
            .OrderByDescending(entry => entry.Id)
            .Select(entry => entry.Currency)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // The schedule for the supply being transferred, for a customer who has never paid a
        // deposit and so has no collection to read terms off.
        currency ??= (await rules.AssessAsync(customer.Class, opened.ServiceType, cancellationToken).ConfigureAwait(false)).Currency;

        var held = customer.DepositHeld;

        if (held <= Money.Zero)
        {
            return (Money.Zero, currency, null);
        }

        var carried = DepositEntry.Carry(
            customer,
            held,
            currency,

            // The ledger row's only account context. Neither account is stored on a deposit entry —
            // see DepositEntry.Carry — so this sentence is what a rep reads on the deposit tab.
            $"Carried from {closed.AccountNumber} to {opened.AccountNumber} on transfer.",
            actor,
            now);

        database.DepositEntries.Add(carried);

        return (carried.Amount, carried.Currency, carried);
    }

    /// <summary>
    /// Refuses a class change dated behind a bill that has already gone out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A 409 rather than a 400: the request is perfectly well formed and the register is simply not
    /// in a state that allows it — the same distinction <c>CustomerDepositService</c> draws between a
    /// mistyped bill id and a bill that cannot take the money.
    /// </para>
    /// <para>
    /// <b>On or after the issue date is allowed, before it is not.</b> A bill issued that morning
    /// covers a period that had already closed, so a class taking effect the same day changes nothing
    /// that has been printed; a class dated even one day earlier would mean the utility priced that
    /// bill on a tariff it now says did not apply, and said nothing about it.
    /// </para>
    /// <para>
    /// Asked through <see cref="IBillDirectory"/>, because this module may not read the billing
    /// schema — and asked as one date rather than through the statement's whole-history call, because
    /// that is the whole question.
    /// </para>
    /// </remarks>
    private async Task RequireNoIssuedBillBehindAsync(Customer customer, DateOnly effectiveOn, CancellationToken cancellationToken)
    {
        var lastIssued = await bills.LastIssuedOnForCustomerAsync(customer.Id, cancellationToken).ConfigureAwait(false);

        if (lastIssued is { } issuedOn && effectiveOn < issuedOn)
        {
            throw new RegistryWorkflowException(
                $"Customer {customer.AccountNumber} was last billed on {issuedOn:yyyy-MM-dd}, so a class change cannot take effect "
                + $"on {effectiveOn:yyyy-MM-dd}. A bill that has gone out was priced on the class the customer held that day; "
                + "re-classifying behind it would make the utility's own document wrong. Date it on or after that day, "
                + "and correct the bill itself if it needs correcting.");
        }
    }

    /// <summary>Refuses a move-out or transfer dated before the account existed.</summary>
    /// <remarks>
    /// The account's opening, not <c>ServiceStartedAt</c>: an account closed while it was still
    /// Pending never carried supply, and demanding that it had been energised would refuse the
    /// perfectly ordinary case of a connection cancelled before the technician arrived.
    /// </remarks>
    private static void RequireServiceHadBegunBy(ServiceAccount account, DateOnly effectiveOn)
    {
        var openedOn = DateOnly.FromDateTime(account.OpenedAt.UtcDateTime);

        if (effectiveOn < openedOn)
        {
            throw new RegistryWorkflowException(
                $"Account {account.AccountNumber} was opened on {openedOn:yyyy-MM-dd}, so service cannot have ended "
                + $"on {effectiveOn:yyyy-MM-dd}. A service period cannot close before it opened.");
        }
    }

    /// <summary>The customer's own account, or a failure that says which of the two things was wrong.</summary>
    /// <remarks>
    /// A missing id is a 404 and somebody else's account is a 400, exactly as
    /// <c>CustomerDepositService</c> separates a bill that does not exist from a bill that is not this
    /// customer's. Answering 404 for both would tell a caller that another customer's account number
    /// does not exist, which is a different claim and an untrue one.
    /// </remarks>
    private async Task<ServiceAccount> RequireOwnAccountAsync(Customer customer, Guid serviceAccountId, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(serviceAccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new ServiceAccountNotFoundException(serviceAccountId);

        if (account.CustomerId != customer.Id)
        {
            throw new RegistryValidationException(
                $"Service account {account.AccountNumber} is served for another customer, "
                + $"so it is not {customer.AccountNumber}'s to move.");
        }

        return account;
    }

    /// <summary>
    /// Loads the customer, checks the caller may move them, applies <paramref name="move"/>, stores
    /// the row and audits it — all inside one unit of work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="DbSet{TEntity}.FindAsync(object?[])"/>, not a query</b>, and the customer is
    /// loaded <i>tracked</i>: a class or status change mutates the row, and a transfer's deposit carry
    /// reads <c>DepositHeld</c> off it. The change-tracker lookup is the rule every write in this
    /// module follows, so a customer added earlier in the same transaction is found.
    /// </para>
    /// <para>
    /// <b>The before snapshot is taken first and the after last</b>, with the sub-services' own moves
    /// in between — so the pair reads as what changed about the customer and their account, whichever
    /// of the five kinds it was. See <see cref="AuditEntityTypes.AccountTransition"/> for why all five
    /// are audited against the register row rather than against whichever thing moved.
    /// </para>
    /// </remarks>
    private Task<AccountTransition> RecordAsync(
        Guid customerId,
        Func<Customer, RegistryActor, DateTimeOffset, CancellationToken, Task<TransitionOutcome>> move,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                RequireTransitionPermission();

                var customer = await database.Customers.FindAsync([customerId], ct).ConfigureAwait(false)
                    ?? throw new CustomerNotFoundException(customerId);

                var (transition, action, before, account) =
                    await move(customer, RegistryActor.Of(currentUser), clock.GetUtcNow(), ct).ConfigureAwait(false);

                database.AccountTransitions.Add(transition);

                audit.Record(
                    action,
                    AuditEntityTypes.AccountTransition,
                    transition.Id.ToString(),
                    before,
                    AccountTransitionSnapshot.Of(customer, account));

                return transition;
            },
            cancellationToken);

    /// <summary>Today on the host's clock, for a caller who did not date the transition themselves.</summary>
    /// <remarks>
    /// UTC, like every other date GridCore stamps. A rep dating a transition themselves is the normal
    /// case for anything back- or forward-dated; this is the "it happened today" default, and the few
    /// hours' disagreement around midnight can only ever put a transition on the day either side of
    /// the one somebody meant — which is why the field is offered at all.
    /// </remarks>
    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    /// <summary>
    /// What the underlying state machine records as its free-text reason.
    /// </summary>
    /// <remarks>
    /// The account history line and the customer's <c>StatusReason</c> are read by people who are not
    /// looking at the transition register — an agent reading a service record back on the telephone —
    /// so the code is spelled out beside whatever the operator wrote rather than left as an id they
    /// would have to go and look up.
    /// </remarks>
    private static string Describe(TransitionReasonCode code, string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? code.ToString() : $"{code}: {notes.Trim()}";

    private void RequireTransitionPermission()
    {
        if (currentUser.HasPermission(Permissions.Customers.Transition))
        {
            return;
        }

        throw new RegistryPermissionException(
            $"Moving a customer between classes or statuses, or moving their service, requires the "
            + $"'{Permissions.Customers.Transition}' permission. These are the changes that alter what a customer is billed.");
    }
}

/// <summary>
/// What one branch of <see cref="CustomerTransitionService"/> hands back: the row it wrote, the
/// audit action it happened under, the snapshot it took <i>before</i> anything moved, and the
/// account the customer ended up with, where there is one.
/// </summary>
/// <remarks>
/// <b>The before snapshot belongs to the branch, not to the shared wrapper.</b> Each kind knows the
/// one instant at which "before" is true: for a move-out it is after the account has been loaded and
/// while it is still open, for a class change it is before the aggregate is touched. A wrapper that
/// took it up front would have to know which account each kind was about before it had looked one
/// up, and a wrapper that took it afterwards would take it too late — the tracked account instance
/// has already moved by then.
/// </remarks>
/// <param name="Transition">The register row.</param>
/// <param name="Action">The audit action it is recorded under.</param>
/// <param name="Before">The customer and account as they stood beforehand.</param>
/// <param name="Account">The account the customer holds afterwards, where the kind is about one.</param>
internal sealed record TransitionOutcome(
    AccountTransition Transition,
    string Action,
    AccountTransitionSnapshot Before,
    ServiceAccount? Account);

/// <summary>
/// The before/after shape a transition is audited as: what the customer was, and which of their
/// accounts the move was about.
/// </summary>
/// <remarks>
/// One shape for all five kinds, deliberately. A class change moves <see cref="Class"/>, a status
/// change moves <see cref="Status"/>, and the three service moves move
/// <see cref="ServiceAccountNumber"/> — so the same pair of snapshots reads correctly whichever kind
/// it was, and an auditor filtering the trail does not have to know the kind before they can read
/// the entry. A dedicated record rather than the entities, so changing either later cannot silently
/// change the meaning of historic entries.
/// </remarks>
/// <param name="CustomerId">Who it happened to.</param>
/// <param name="AccountNumber">The number they quote.</param>
/// <param name="Class">Residential or commercial, at the time of the snapshot.</param>
/// <param name="Status">Where they stood.</param>
/// <param name="DepositHeld">What the utility was holding — unchanged across a transfer, which is the claim.</param>
/// <param name="ClassEffectiveOn">The day the class in this snapshot applies from.</param>
/// <param name="StatusEffectiveOn">The day the status in it applies from.</param>
/// <param name="ServiceAccountNumber">The account the move was about, where it was about one.</param>
/// <param name="ServiceAccountStatus">Where that account stood.</param>
public sealed record AccountTransitionSnapshot(
    Guid CustomerId,
    string AccountNumber,
    CustomerClass Class,
    CustomerStatus Status,
    decimal DepositHeld,
    DateOnly? ClassEffectiveOn,
    DateOnly? StatusEffectiveOn,
    string? ServiceAccountNumber,
    string? ServiceAccountStatus)
{
    /// <summary>Takes a snapshot of <paramref name="customer"/> and, where there is one, <paramref name="account"/>.</summary>
    public static AccountTransitionSnapshot Of(Customer customer, ServiceAccount? account)
    {
        ArgumentNullException.ThrowIfNull(customer);

        return new AccountTransitionSnapshot(
            customer.Id,
            customer.AccountNumber,
            customer.Class,
            customer.Status,
            customer.DepositHeld,
            customer.ClassEffectiveOn,
            customer.StatusEffectiveOn,
            account?.AccountNumber,
            account?.Status.ToString());
    }
}
