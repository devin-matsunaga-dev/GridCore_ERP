using GridCore.Contracts.Directories;
using GridCore.Contracts.Events;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Deposits;

/// <summary>What a caller supplies to take a deposit.</summary>
/// <param name="Amount">How much was taken. Positive, to the cent.</param>
/// <param name="IsInterestBearing">Whether the holding earns interest. Stored, never accrued in the MVP.</param>
/// <param name="Reason">Why, for the record a rep reads back.</param>
public sealed record CollectDepositInput(decimal Amount, bool IsInterestBearing = false, string? Reason = null);

/// <summary>What a caller supplies to put a held deposit against a bill.</summary>
/// <param name="BillId">The bill to settle, in Billing's schema.</param>
/// <param name="Amount">How much of the deposit to apply. Positive, to the cent.</param>
/// <param name="Reason">Why, for the record a rep reads back.</param>
public sealed record ApplyDepositInput(Guid BillId, decimal Amount, string? Reason = null);

/// <summary>What a caller supplies to give a deposit back.</summary>
/// <param name="Amount">How much to return. Positive, to the cent.</param>
/// <param name="Reason">Why, for the record a rep reads back.</param>
public sealed record RefundDepositInput(decimal Amount, string? Reason = null);

/// <summary>
/// One customer's deposit as a screen reads it: what is held, what the schedule asks of them, and
/// every movement behind the figure.
/// </summary>
/// <param name="CustomerId">Whose deposit.</param>
/// <param name="AccountNumber">The number they quote.</param>
/// <param name="Balance">What the utility holds — <see cref="Customer.DepositHeld"/>, which these entries add up to.</param>
/// <param name="Currency">ISO 4217 code the balance is expressed in.</param>
/// <param name="Assessment">What the schedule asks of a customer of this class, so a screen can say whether they are short.</param>
/// <param name="IsInterestBearing">Whether the most recent collection was taken on interest-bearing terms.</param>
/// <param name="Entries">Every movement, newest first.</param>
public sealed record DepositLedger(
    Guid CustomerId,
    string AccountNumber,
    decimal Balance,
    string Currency,
    DepositAssessment Assessment,
    bool IsInterestBearing,
    IReadOnlyList<DepositEntry> Entries);

/// <summary>The deposit lifecycle: collect, hold, apply to a bill, refund. The module's own surface.</summary>
public interface ICustomerDepositService
{
    /// <summary>One customer's deposit ledger, newest movement first.</summary>
    /// <exception cref="CustomerNotFoundException">There is no such customer.</exception>
    Task<DepositLedger> GetAsync(Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>Takes a deposit and holds it against the customer's account.</summary>
    Task<DepositEntry> CollectAsync(Guid customerId, CollectDepositInput input, CancellationToken cancellationToken = default);

    /// <summary>Puts part or all of the held deposit against a bill the customer owes.</summary>
    Task<DepositEntry> ApplyAsync(Guid customerId, ApplyDepositInput input, CancellationToken cancellationToken = default);

    /// <summary>Gives part or all of the held deposit back to the customer.</summary>
    Task<DepositEntry> RefundAsync(Guid customerId, RefundDepositInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// The deposit ledger over the customers schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every movement is permission-gated, audited and published</b> — invariant 5 for the first two,
/// and invariant 2 for the third. The three events are what let Finance post the double entry
/// WORK_PACKAGES.md specifies (collection Dr Cash / Cr Customer Deposits, refund the reverse,
/// application Dr Customer Deposits / Cr AR) without this module ever knowing a <c>finance</c>
/// schema exists — and an application is the one event two modules claim, because Billing has to
/// reduce what the bill is owed as well.
/// </para>
/// <para>
/// <b>The gate is demanded here as well as on the route, and that is not belt-and-braces.</b> The
/// endpoints are entirely deposit acts, so they carry <see cref="Permissions.Customers.Deposit"/> —
/// unlike WP-2.8's intake, where only part of the request took money and the route could not see
/// which. But the intake wizard calls <see cref="CollectAsync"/> in process, and CONVENTIONS.md's
/// rule is that services enforce permissions rather than trusting the endpoint that called them.
/// One check here covers both callers.
/// </para>
/// <para>
/// <b>Whether a bill can take the money is Billing's answer, asked through
/// <see cref="IBillDirectory"/>.</b> This module may not read the billing schema, and it must not
/// guess: a bill's balance since WP-2.4 is its printed total plus every correction since, less what
/// has been paid. The directory computes it, and the bill refuses an overpayment again when it
/// consumes the event — the check here is what turns a race into a 409 the rep can act on rather
/// than a message parked on a dead-letter queue.
/// </para>
/// </remarks>
public sealed class CustomerDepositService(
    CustomersDbContext database,
    IDepositRuleService rules,
    IBillDirectory bills,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    IEventPublisher events,
    ICurrentUser currentUser,
    TimeProvider clock) : ICustomerDepositService
{
    /// <summary>The most movements <see cref="GetAsync"/> will return, whatever a customer's history holds.</summary>
    public const int MaxEntries = 200;

    /// <inheritdoc />
    public async Task<DepositLedger> GetAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var customer = await database.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == customerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CustomerNotFoundException(customerId);

        var entries = await database.DepositEntries
            .AsNoTracking()
            .Where(entry => entry.CustomerId == customerId)

            // Ordered by key: ids are Guid v7, so the primary-key index already orders
            // chronologically on Postgres and on the fast tier's SQLite alike.
            .OrderByDescending(entry => entry.Id)
            .Take(MaxEntries)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var assessment = await rules.AssessAsync(customer.Class, cancellationToken).ConfigureAwait(false);

        // The terms come from the most recent collection, which is the one under which the money now
        // held was taken. A customer who has never paid one has no terms, and false is what "no
        // interest is accruing" reads as either way.
        var latestCollection = entries.FirstOrDefault(entry => entry.Kind is DepositEntryKind.Collected);

        return new DepositLedger(
            customer.Id,
            customer.AccountNumber,
            customer.DepositHeld,
            latestCollection?.Currency ?? assessment.Currency,
            assessment,
            latestCollection?.IsInterestBearing ?? false,
            entries);
    }

    /// <inheritdoc />
    public Task<DepositEntry> CollectAsync(Guid customerId, CollectDepositInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return MoveAsync(
            customerId,
            async (customer, actor, now, ct) =>
            {
                var assessment = await rules.AssessAsync(customer.Class, ct).ConfigureAwait(false);

                var entry = DepositEntry.Collect(
                    customer,
                    input.Amount,
                    assessment.Currency,
                    input.IsInterestBearing,
                    input.Reason,
                    actor,
                    now);

                // WP-2.8's shape kept: the entry carries what was asked for beside what was taken and
                // the rule that said so, which is the only place that difference is recorded.
                audit.Record(
                    AuditActions.CustomerDepositCollected,
                    AuditEntityTypes.Customer,
                    customer.Id.ToString(),
                    before: null,
                    after: new DepositCollectionSnapshot(
                        customer.Id,
                        customer.AccountNumber,
                        assessment.CustomerClass,
                        assessment.RuleId,
                        assessment.Amount,
                        entry.Amount,
                        entry.BalanceAfter,
                        entry.IsInterestBearing));

                await events.PublishAsync(
                    CustomerDepositCollected.For(
                        now,
                        entry.Id,
                        customer.Id,
                        customer.AccountNumber,
                        entry.Amount,
                        entry.BalanceAfter,
                        entry.Currency,
                        entry.IsInterestBearing),
                    ct).ConfigureAwait(false);

                return entry;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<DepositEntry> ApplyAsync(Guid customerId, ApplyDepositInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return MoveAsync(
            customerId,
            async (customer, actor, now, ct) =>
            {
                var bill = await bills.FindAsync(input.BillId, ct).ConfigureAwait(false)
                    ?? throw new RegistryValidationException(
                        $"Bill '{input.BillId}' was not found, so a deposit cannot be applied to it.");

                RequireTheBillCanTakeIt(customer, bill, input.Amount);

                var entry = DepositEntry.Apply(
                    customer,
                    bill.Id,
                    bill.BillNumber,
                    bill.ServiceAccountId,
                    input.Amount,

                    // The BILL's currency, not the schedule's: the receivable being relieved is
                    // denominated in what the bill was raised in, and a posting that mixed the two
                    // would balance in arithmetic and mean nothing in accounting.
                    bill.Currency,
                    input.Reason,
                    actor,
                    now);

                audit.Record(
                    AuditActions.CustomerDepositApplied,
                    AuditEntityTypes.Customer,
                    customer.Id.ToString(),
                    before: null,
                    after: new DepositApplicationSnapshot(
                        customer.Id,
                        customer.AccountNumber,
                        bill.Id,
                        bill.BillNumber,
                        bill.Balance,
                        entry.Amount,
                        entry.BalanceAfter));

                await events.PublishAsync(
                    CustomerDepositApplied.For(
                        now,
                        entry.Id,
                        customer.Id,
                        bill.ServiceAccountId,
                        bill.Id,
                        bill.BillNumber,
                        entry.Amount,
                        entry.BalanceAfter,
                        entry.Currency),
                    ct).ConfigureAwait(false);

                return entry;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<DepositEntry> RefundAsync(Guid customerId, RefundDepositInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return MoveAsync(
            customerId,
            async (customer, actor, now, ct) =>
            {
                var assessment = await rules.AssessAsync(customer.Class, ct).ConfigureAwait(false);

                // "A refund cannot exceed the held balance" is not checked here: DepositEntry.Refund
                // moves the balance through Customer.RecordDepositMovement, which refuses to go below
                // zero. One guard, covering refunds and bill applications alike — a rule per kind is
                // a rule a fourth kind gets added without.
                var entry = DepositEntry.Refund(customer, input.Amount, assessment.Currency, input.Reason, actor, now);

                audit.Record(
                    AuditActions.CustomerDepositRefunded,
                    AuditEntityTypes.Customer,
                    customer.Id.ToString(),
                    before: null,
                    after: new DepositRefundSnapshot(
                        customer.Id,
                        customer.AccountNumber,
                        entry.Amount,
                        entry.BalanceAfter,
                        entry.Reason));

                await events.PublishAsync(
                    CustomerDepositRefunded.For(
                        now,
                        entry.Id,
                        customer.Id,
                        customer.AccountNumber,
                        entry.Amount,
                        entry.BalanceAfter,
                        entry.Currency,
                        entry.Reason),
                    ct).ConfigureAwait(false);

                return entry;
            },
            cancellationToken);
    }

    /// <summary>
    /// Refuses an application the bill cannot accept.
    /// </summary>
    /// <remarks>
    /// Two questions, and they fail differently on purpose. A bill belonging to somebody else is a
    /// mistyped id — a validation failure. A bill that is a draft, cancelled or already settled, or
    /// one with less outstanding than the amount offered, is a workflow conflict: the request was
    /// well formed and the register is simply not in a state that allows it.
    /// </remarks>
    private static void RequireTheBillCanTakeIt(Customer customer, BillSummary bill, decimal amount)
    {
        if (bill.CustomerId != customer.Id)
        {
            throw new RegistryValidationException(
                $"Bill {bill.BillNumber} is owed by another customer, so {customer.AccountNumber}'s deposit cannot settle it.");
        }

        if (!bill.IsOutstanding)
        {
            throw new RegistryWorkflowException(
                $"Bill {bill.BillNumber} is {bill.Status} and is not owed, so there is nothing for a deposit to settle.");
        }

        if (amount > bill.Balance)
        {
            // Refused rather than absorbed, the same call Bill.RecordPayment makes about an
            // overpayment: money left over would be a credit with no record of where it went, and a
            // deposit is the one balance in GridCore that already has somewhere to sit.
            throw new RegistryWorkflowException(
                $"Bill {bill.BillNumber} has {bill.Balance:0.00} outstanding; applying {amount:0.00} of the deposit is more than is owed. "
                + "Apply the outstanding amount or less and leave the rest on deposit.");
        }
    }

    /// <summary>
    /// Loads the customer, checks the caller may move money, applies <paramref name="move"/> and
    /// stores the entry — all inside one unit of work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="DbSet{TEntity}.FindAsync(object?[])"/>, not a query.</b> The intake wizard
    /// (WP-2.8) registers a customer and collects their deposit inside one transaction, and nothing
    /// is saved until the outermost unit of work commits — so a customer added moments earlier is
    /// tracked but not yet a row any <c>WHERE</c> could find. <c>FindAsync</c> looks in the change
    /// tracker before it looks in the database, which is exactly the difference between an intake
    /// that works and one that 404s on the deposit it just assessed.
    /// </para>
    /// <para>
    /// The customer is loaded <i>tracked</i>, deliberately: the entry factories move
    /// <c>DepositHeld</c>, and an untracked customer would have its new balance discarded at commit,
    /// leaving a ledger row against a balance that never changed.
    /// </para>
    /// </remarks>
    private Task<DepositEntry> MoveAsync(
        Guid customerId,
        Func<Customer, RegistryActor, DateTimeOffset, CancellationToken, Task<DepositEntry>> move,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                RequireDepositPermission();

                var customer = await database.Customers.FindAsync([customerId], ct).ConfigureAwait(false)
                    ?? throw new CustomerNotFoundException(customerId);

                var entry = await move(customer, RegistryActor.Of(currentUser), clock.GetUtcNow(), ct).ConfigureAwait(false);

                database.DepositEntries.Add(entry);

                return entry;
            },
            cancellationToken);

    private void RequireDepositPermission()
    {
        if (currentUser.HasPermission(Permissions.Customers.Deposit))
        {
            return;
        }

        throw new RegistryPermissionException(
            $"Moving a customer's security deposit requires the '{Permissions.Customers.Deposit}' permission. "
            + "Somebody who holds it has to take, apply or refund it.");
    }
}

/// <summary>
/// The shape a deposit collection is audited as: what the schedule asked for, beside what was taken,
/// and which rule said so.
/// </summary>
/// <param name="CustomerId">Who paid it.</param>
/// <param name="AccountNumber">The number they quote.</param>
/// <param name="CustomerClass">The class assessed.</param>
/// <param name="RuleId">The reference row the assessed figure came from.</param>
/// <param name="AssessedAmount">What the schedule asks of a customer of that class.</param>
/// <param name="CollectedAmount">What was actually taken.</param>
/// <param name="BalanceAfter">What the utility holds once it was.</param>
/// <param name="IsInterestBearing">The terms it was taken under.</param>
public sealed record DepositCollectionSnapshot(
    Guid CustomerId,
    string AccountNumber,
    CustomerClass CustomerClass,
    Guid RuleId,
    decimal AssessedAmount,
    decimal CollectedAmount,
    decimal BalanceAfter,
    bool IsInterestBearing);

/// <summary>The shape an application is audited as: which bill it settled and what it left behind.</summary>
/// <param name="CustomerId">Whose deposit.</param>
/// <param name="AccountNumber">The number they quote.</param>
/// <param name="BillId">The bill settled.</param>
/// <param name="BillNumber">Its number, as printed.</param>
/// <param name="BillBalanceBefore">What was outstanding on it when the deposit was applied.</param>
/// <param name="AppliedAmount">How much of the deposit went to it.</param>
/// <param name="BalanceAfter">What the utility still holds.</param>
public sealed record DepositApplicationSnapshot(
    Guid CustomerId,
    string AccountNumber,
    Guid BillId,
    string BillNumber,
    decimal BillBalanceBefore,
    decimal AppliedAmount,
    decimal BalanceAfter);

/// <summary>The shape a refund is audited as. Money leaving the building, so it says why.</summary>
/// <param name="CustomerId">Who it went back to.</param>
/// <param name="AccountNumber">The number they quote.</param>
/// <param name="RefundedAmount">How much was returned.</param>
/// <param name="BalanceAfter">What the utility still holds.</param>
/// <param name="Reason">Why, in the operator's words.</param>
public sealed record DepositRefundSnapshot(
    Guid CustomerId,
    string AccountNumber,
    decimal RefundedAmount,
    decimal BalanceAfter,
    string? Reason);
