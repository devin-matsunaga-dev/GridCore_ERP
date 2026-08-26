using GridCore.Contracts.Directories;
using GridCore.Contracts.Events;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Fees;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Rating;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.Features.Bills;

/// <summary>What a caller supplies to bill a reading cycle.</summary>
/// <param name="CycleCode">The reading cycle to bill, e.g. <c>2026-08</c>.</param>
public sealed record RunBillingInput(string CycleCode);

/// <summary>What a caller supplies to issue a bill.</summary>
/// <param name="IssuedOn">The day it goes out. Defaults to today.</param>
/// <param name="DueDate">
/// When payment falls due. Defaults to <see cref="BillingTerms.DueDays"/> after the issue date.
/// </param>
/// <param name="Reason">What to record against the transition.</param>
public sealed record IssueBillInput(DateOnly? IssuedOn = null, DateOnly? DueDate = null, string? Reason = null);

/// <summary>What a caller supplies to withdraw a bill.</summary>
/// <param name="Reason">Why. Required — cancelling a bill removes money the utility was owed.</param>
public sealed record CancelBillInput(string Reason);

/// <summary>What a caller supplies to correct an issued bill.</summary>
/// <param name="Kind">Which way the money moves — money off the bill, or money on.</param>
/// <param name="Amount">How much, always positive. The kind carries the direction, not the sign.</param>
/// <param name="Reason">Why. Required — this is the sensitive action invariant 5 is about.</param>
public sealed record AdjustBillInput(BillAdjustmentKind Kind, decimal Amount, string Reason);

/// <summary>What arrives when an approved payment is applied to a bill.</summary>
/// <param name="Amount">How much was approved. Always positive.</param>
/// <param name="PaymentId">The payment in Payments' schema, so the two records can be tied together.</param>
/// <param name="ProviderReference">The provider's reference, which a bank reconciliation matches on.</param>
/// <param name="Reason">What to record against the transition.</param>
public sealed record RecordBillPaymentInput(
    decimal Amount,
    Guid PaymentId,
    string ProviderReference,
    string? Reason = null);

/// <summary>
/// What arrives when a customer's security deposit is put against a bill (WP-2.12).
/// </summary>
/// <remarks>
/// Its own input rather than a <see cref="RecordBillPaymentInput"/> with a deposit id squeezed into
/// the payment field. The two settle a bill the same way — money against the amount paid, never a
/// bill adjustment — but they are different facts about where the money came from, and the audit
/// trail says so with a different action.
/// </remarks>
/// <param name="Amount">How much of the deposit was applied. Always positive.</param>
/// <param name="DepositEntryId">The ledger entry in the Customers schema, so the two records can be tied together.</param>
/// <param name="Reason">What to record against the transition.</param>
public sealed record RecordBillDepositInput(
    decimal Amount,
    Guid DepositEntryId,
    string? Reason = null);

/// <summary>What a caller supplies to review overdue bills.</summary>
/// <param name="AsOf">The day to judge against. Defaults to today.</param>
public sealed record OverdueReviewInput(DateOnly? AsOf = null);

/// <summary>How the billing register is filtered.</summary>
/// <param name="ServiceAccountId">Only bills raised against this account.</param>
/// <param name="CustomerId">Only bills owed by this customer.</param>
/// <param name="Status">Only bills in this status.</param>
/// <param name="OutstandingOnly">Only money still owed — the AR worklist, without naming three statuses.</param>
/// <param name="CycleCode">Only bills from this billing run.</param>
/// <param name="Limit">Most rows to return.</param>
/// <param name="IncludeAdjustments">
/// Load each row's corrections as well. Off by default, and it is the lines that are the reason
/// why: a page of fifty bills does not want two hundred lines, and adjustments are a second
/// collection with the same objection. A caller asking for a bounded window of one customer's
/// bills — the 360° page's timeline, which shows a correction as an event in its own right — has
/// no such page, and the alternative is one detail request per bill.
/// </param>
public sealed record BillQuery(
    Guid? ServiceAccountId = null,
    Guid? CustomerId = null,
    BillStatus? Status = null,
    bool? OutstandingOnly = null,
    string? CycleCode = null,
    int Limit = 50,
    bool IncludeAdjustments = false);

/// <summary>A reading a billing run did not bill, and why.</summary>
/// <remarks>
/// Reported rather than silently dropped. A run that billed nine of twelve premises and said nothing
/// about the other three is a run whose output nobody can reconcile against the reading cycle — and
/// "why was this house not billed" is the question a billing officer opens the screen to ask.
/// </remarks>
/// <param name="MeterReadingId">The reading that was not billed.</param>
/// <param name="ServiceLocationId">The premise it was taken at.</param>
/// <param name="MeterNumber">The meter that produced it.</param>
/// <param name="Reason">Why it was skipped, in words a billing officer can act on.</param>
public sealed record SkippedReading(Guid MeterReadingId, Guid ServiceLocationId, string MeterNumber, string Reason);

/// <summary>What a billing run produced.</summary>
/// <param name="CycleCode">The reading cycle billed.</param>
/// <param name="Bills">Every bill raised, as a draft.</param>
/// <param name="Skipped">Every reading that was not billed, and why.</param>
public sealed record BillingRunResult(string CycleCode, IReadOnlyList<Bill> Bills, IReadOnlyList<SkippedReading> Skipped)
{
    /// <summary>How many bills were raised.</summary>
    public int Raised => Bills.Count;

    /// <summary>What they come to in total.</summary>
    public decimal TotalBilled => Money.Total(Bills.Select(bill => bill.TotalAmount));

    /// <summary>How many readings were passed over.</summary>
    public int SkippedCount => Skipped.Count;

    /// <summary>How many were passed over for each reason, for the audit entry and the response.</summary>
    public IReadOnlyDictionary<string, int> ByReason =>
        Skipped
            .GroupBy(skipped => skipped.Reason, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}

/// <summary>What an overdue review found.</summary>
/// <param name="AsOf">The day judged against.</param>
/// <param name="Bills">Every bill that moved to <see cref="BillStatus.Overdue"/>.</param>
public sealed record OverdueReviewResult(DateOnly AsOf, IReadOnlyList<Bill> Bills)
{
    /// <summary>How many bills moved.</summary>
    public int MarkedOverdue => Bills.Count;

    /// <summary>What is now overdue, in total.</summary>
    public decimal TotalOverdue => Money.Total(Bills.Select(bill => bill.Balance));
}

/// <summary>The terms the utility bills on.</summary>
public static class BillingTerms
{
    /// <summary>
    /// Days a customer has to pay. Twenty-one is the ordinary utility term and is deliberately a
    /// constant rather than configuration: a per-account term is a real feature with a real screen
    /// behind it, and inventing half of one here would be a setting nobody set.
    /// </summary>
    public const int DueDays = 21;
}

/// <summary>The billing register. Endpoints are a thin layer over it.</summary>
public interface IBillService
{
    /// <summary>Raises a draft bill for every billable reading in a cycle.</summary>
    /// <exception cref="BillingValidationException">The cycle code is missing.</exception>
    Task<BillingRunResult> RunAsync(RunBillingInput input, CancellationToken cancellationToken = default);

    /// <summary>Issues a draft bill, publishing <see cref="BillIssued"/> for Finance to post.</summary>
    /// <exception cref="BillNotFoundException">There is no bill with that id.</exception>
    /// <exception cref="BillingWorkflowException">The bill is not a draft.</exception>
    Task<Bill> IssueAsync(Guid billId, IssueBillInput input, CancellationToken cancellationToken = default);

    /// <summary>Withdraws a bill.</summary>
    /// <exception cref="BillNotFoundException">There is no bill with that id.</exception>
    /// <exception cref="BillingWorkflowException">The bill is already settled or already cancelled.</exception>
    Task<Bill> CancelAsync(Guid billId, CancelBillInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Corrects what an issued bill is owed, publishing <see cref="BillAdjusted"/> for Finance to
    /// post against the receivable. Sensitive: gated on <c>billing.adjust</c> and audited with the
    /// bill before and after.
    /// </summary>
    /// <exception cref="BillNotFoundException">There is no bill with that id.</exception>
    /// <exception cref="BillingWorkflowException">
    /// The bill is not owed, or the credit is larger than its balance.
    /// </exception>
    /// <exception cref="BillingValidationException">The correction is not one a bill can carry.</exception>
    Task<Bill> AdjustAsync(Guid billId, AdjustBillInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies money that has arrived to a bill, moving it to
    /// <see cref="BillStatus.PartiallyPaid"/> or <see cref="BillStatus.Paid"/>.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="PaymentApprovedConsumer"/> and by nothing else — there is deliberately
    /// no endpoint. Money arriving is the Payments module's fact to state; a route here would be a
    /// second way to mark a bill paid, with no payment record behind it.
    /// </remarks>
    /// <exception cref="BillNotFoundException">There is no bill with that id.</exception>
    /// <exception cref="BillingWorkflowException">
    /// The bill is not owed, or the payment is more than is outstanding on it.
    /// </exception>
    /// <exception cref="BillingValidationException">The amount is not positive, or is finer than a cent.</exception>
    Task<Bill> RecordPaymentAsync(Guid billId, RecordBillPaymentInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reduces what a bill is owed because a customer's security deposit was applied to it.
    /// </summary>
    /// <remarks>
    /// <b>A payment-side effect, not a bill mutation.</b> WP-2.4's rule holds — money moving is not
    /// a lifecycle state — so the bill's adjustment trail is untouched and only the amount paid
    /// moves, exactly as it does for an approved payment.
    /// </remarks>
    Task<Bill> RecordDepositAsync(Guid billId, RecordBillDepositInput input, CancellationToken cancellationToken = default);

    /// <summary>Moves every outstanding bill past its due date to <see cref="BillStatus.Overdue"/>.</summary>
    Task<OverdueReviewResult> ReviewOverdueAsync(OverdueReviewInput input, CancellationToken cancellationToken = default);

    /// <summary>The billing register, newest first. Lines are not loaded.</summary>
    Task<IReadOnlyList<Bill>> ListAsync(BillQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// One bill with its lines and its adjustments, or <see langword="null"/> when there is no such id.
    /// </summary>
    Task<Bill?> FindAsync(Guid billId, CancellationToken cancellationToken = default);
}

/// <summary>The billing register over the billing schema.</summary>
/// <remarks>
/// <para>
/// Every write runs inside <see cref="IUnitOfWork.ExecuteAsync"/> and never calls
/// <c>SaveChanges</c> itself, so a bill, its audit entry and its <see cref="BillIssued"/> outbox row
/// are one transaction — invariants 1 and 2.
/// </para>
/// <para>
/// The arithmetic is not here. <see cref="RateEngine"/> prices consumption and
/// <see cref="RatePlanSelector"/> chooses the tariff version; this service assembles what they need
/// out of the database and two cross-module directories. That split is what CONVENTIONS.md's ⚡
/// rules ask for: the part a customer will dispute is pure, and the part that needs a row is thin.
/// </para>
/// <para>
/// It reads nothing outside its own schema. Readings arrive through
/// <see cref="IMeterReadingDirectory"/> and accounts through <see cref="IServiceAccountDirectory"/>,
/// both interfaces in <c>Contracts</c> — this module has never heard of a <c>metering</c> or a
/// <c>customers</c> schema.
/// </para>
/// </remarks>
public sealed class BillService(
    BillingDbContext database,
    IMeterReadingDirectory readings,
    IServiceAccountDirectory accounts,
    IBillNumberGenerator numbers,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    IEventPublisher events,
    ICurrentUser currentUser,
    TimeProvider clock) : IBillService
{
    /// <summary>The largest page a list will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// Most readings one billing run will consider. A run is one transaction, and a cycle that
    /// outgrows this wants splitting into rounds rather than a longer transaction — the same call
    /// WP-2.2 made about a reading route.
    /// </summary>
    public const int MaxRunSize = 500;

    /// <summary>Most bills one overdue review will move in a pass.</summary>
    public const int MaxReviewSize = 500;

    /// <inheritdoc />
    public Task<BillingRunResult> RunAsync(RunBillingInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var cycleCode = RegistryText.Clean(input.CycleCode, Bill.CycleCodeLength)
                    ?? throw new BillingValidationException("A billing run must name the reading cycle to bill.");

                var cycle = await readings.ForCycleAsync(cycleCode, MaxRunSize, ct).ConfigureAwait(false);

                if (cycle.Count is 0)
                {
                    // Not a conflict: an unread cycle is a cycle nobody has run yet, and telling the
                    // caller "nothing to bill" beats a 409 they cannot act on.
                    return new BillingRunResult(cycleCode, [], []);
                }

                // One boundary call for the whole cycle rather than one per meter — the batched
                // shape WP-2.1 established for premises, applied to the accounts on them.
                var openAccounts = await accounts
                    .FindOpenAtLocationsAsync([.. cycle.Select(reading => reading.ServiceLocationId)], ct)
                    .ConfigureAwait(false);

                var tariffs = await TariffsAsync(openAccounts.Values, ct).ConfigureAwait(false);
                var plans = await database.RatePlans.AsNoTracking().Include(plan => plan.Tiers).ToListAsync(ct).ConfigureAwait(false);

                // Fees waiting against the accounts this cycle will bill (WP-2.16), in one query for
                // the whole run — the batched shape the accounts and the tariffs above already use.
                // Tracked, not AsNoTracking: landing one on a bill moves it to Billed in this same
                // transaction.
                var waitingFees = await PendingChargesAsync(openAccounts.Values, ct).ConfigureAwait(false);

                // Which accounts have already been billed for this cycle. Checked up front, in one
                // query: ux_bills_account_cycle guarantees it, this is what turns the guarantee into
                // a reason a billing officer can read.
                var alreadyBilled = await database.Bills
                    .AsNoTracking()
                    .Where(bill => bill.CycleCode == cycleCode)
                    .Select(bill => bill.ServiceAccountId)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                var billed = alreadyBilled.ToHashSet();

                var raised = new List<Bill>();
                var skipped = new List<SkippedReading>();
                var actor = RegistryActor.Of(currentUser);

                // The whole batch's numbers, reserved in one call. Bills added to the context are
                // invisible to the query that issues the next number, so asking once per bill would
                // hand out the same number every time — see RegistryNumberSeries.NextManyAsync.
                var reserved = await numbers.NextBillNumbersAsync(cycle.Count, ct).ConfigureAwait(false);
                var nextNumber = 0;

                foreach (var reading in cycle)
                {
                    if (Reject(reading, openAccounts, billed) is { } reason)
                    {
                        skipped.Add(new SkippedReading(reading.Id, reading.ServiceLocationId, reading.MeterNumber, reason));

                        continue;
                    }

                    var account = openAccounts[reading.ServiceLocationId];
                    var periodStart = DateOnly.FromDateTime(reading.PreviousReadingDate!.Value.UtcDateTime);
                    var periodEnd = DateOnly.FromDateTime(reading.ReadingDate.UtcDateTime);
                    var code = tariffs[account.Id];

                    // Effective dating: the version in force at the END OF THE PERIOD, never today.
                    // A run in September for August's consumption bills August's rates, whatever the
                    // tariff says by the time somebody presses the button.
                    if (RatePlanSelector.InForceOn(plans, code, periodEnd) is not { } plan)
                    {
                        skipped.Add(new SkippedReading(
                            reading.Id,
                            reading.ServiceLocationId,
                            reading.MeterNumber,
                            $"Rate plan '{code}' was not in force on {periodEnd:yyyy-MM-dd}"));

                        continue;
                    }

                    // THE FEES LAND HERE, on the next bill the account is sent — which is what
                    // "it lands on the next bill" means, and why a charge raised at the desk on
                    // Tuesday needs nobody to remember it on the 28th. A reading that is skipped
                    // above raises no bill, so its account's fees stay pending for the next cycle.
                    var fees = waitingFees.TryGetValue(account.Id, out var pending) ? pending : [];

                    var bill = Bill.Calculate(
                        reserved[nextNumber++],
                        account,
                        new BilledReading(reading.Id, reading.MeterId, reading.MeterNumber, reading.PreviousReading, reading.Reading),
                        RateEngine.Calculate(plan, [.. plan.Tiers], reading.Consumption!.Value),
                        periodStart,
                        periodEnd,
                        actor,
                        now,
                        cycleCode,
                        [.. fees.Select(fee => fee.AsBillLine())]);

                    // After the bill exists, so a charge is never marked billed against a document
                    // that Calculate then refused to produce.
                    foreach (var fee in fees)
                    {
                        fee.MarkBilled(bill.Id, bill.BillNumber, now);
                    }

                    database.Bills.Add(bill);
                    raised.Add(bill);

                    // An account cannot be billed twice inside one run either — the same premise
                    // read by two meters in one cycle would otherwise raise two bills that the
                    // unique index refuses at commit, losing the whole run instead of one reading.
                    billed.Add(account.Id);
                }

                var result = new BillingRunResult(cycleCode, raised, skipped);

                // ONE audit entry for the run, not one per bill — WP-2.2's call about a reading
                // cycle, and the same reasoning: a run is one act, and what an auditor asks is "who
                // billed the August cycle and what came out of it". Each bill is its own row
                // stamped with who raised it, and issuing one is audited separately because that is
                // where the money starts being owed.
                audit.Record(
                    AuditActions.BillingRunExecuted,
                    AuditEntityTypes.BillingRun,
                    cycleCode,
                    before: null,
                    after: BillingRunSnapshot.Of(result));

                return result;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Bill> IssueAsync(Guid billId, IssueBillInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();
                var today = DateOnly.FromDateTime(now.UtcDateTime);

                var bill = await LoadAsync(billId, ct).ConfigureAwait(false);
                var before = BillSnapshot.Of(bill);

                var issuedOn = input.IssuedOn ?? today;

                bill.Issue(issuedOn, input.DueDate ?? issuedOn.AddDays(BillingTerms.DueDays), RegistryActor.Of(currentUser), now, input.Reason);

                audit.Record(
                    AuditActions.BillIssued,
                    AuditEntityTypes.Bill,
                    bill.Id.ToString(),
                    before,
                    BillSnapshot.Of(bill));

                // The one event this module raises. Finance consumes it and posts the receivable
                // (Dr AR / Cr Revenue) — it is issuing that makes a bill money the utility is owed,
                // which is why a draft publishes nothing.
                await events.PublishAsync(
                        BillIssued.For(
                            now,
                            bill.Id,
                            bill.BillNumber,
                            bill.ServiceAccountId,
                            bill.CustomerId,
                            bill.PeriodStart,
                            bill.PeriodEnd,
                            bill.DueDate!.Value,
                            bill.TotalAmount,
                            bill.Currency,
                            bill.FeeAmount),
                        ct)
                    .ConfigureAwait(false);

                return bill;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Bill> CancelAsync(Guid billId, CancelBillInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var bill = await LoadAsync(billId, ct).ConfigureAwait(false);
                var before = BillSnapshot.Of(bill);

                bill.Cancel(input.Reason, RegistryActor.Of(currentUser), now);

                // Invariant 5 in the shape it takes here: withdrawing money the utility was owed is
                // audited with the state it was in and the reason it was withdrawn.
                audit.Record(
                    AuditActions.BillCancelled,
                    AuditEntityTypes.Bill,
                    bill.Id.ToString(),
                    before,
                    BillSnapshot.Of(bill));

                // No event. Finance posted a receivable on BillIssued and reversing it is a journal
                // decision WP-2.6 owns — a cancellation event raised now would be one nothing
                // consumes and one WP-2.6 would have to honour blind.
                return bill;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Bill> AdjustAsync(Guid billId, AdjustBillInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var bill = await LoadAsync(billId, ct).ConfigureAwait(false);
                var before = BillSnapshot.Of(bill);

                var adjustment = bill.Adjust(input.Kind, input.Amount, input.Reason, RegistryActor.Of(currentUser), now);

                // INVARIANT 5, in the shape this work package is about. The gate is on the endpoint
                // (billing.adjust, which the Billing role and Managers hold and nobody else does);
                // this is the other half, and the before/after is the bill rather than the entry —
                // "what did this change about what the customer owes" is the question, and the entry
                // on its own cannot answer it.
                audit.Record(
                    AuditActions.BillAdjusted,
                    AuditEntityTypes.Bill,
                    bill.Id.ToString(),
                    before,
                    BillSnapshot.Of(bill));

                // Finance posted a receivable on BillIssued; this is the correction to it. Published
                // rather than left for a later work package — unlike a cancellation, an adjustment
                // states a signed change to a known amount, which is a fact WP-2.6 can post from
                // without having to guess what was meant.
                await events.PublishAsync(
                        BillAdjusted.For(
                            now,
                            bill.Id,
                            bill.BillNumber,
                            bill.ServiceAccountId,
                            bill.CustomerId,
                            adjustment.Id,
                            adjustment.Kind.ToString(),
                            adjustment.Amount,
                            bill.AmountDue,
                            bill.Currency,
                            adjustment.Reason),
                        ct)
                    .ConfigureAwait(false);

                return bill;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Bill> RecordPaymentAsync(Guid billId, RecordBillPaymentInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // INVARIANT 1, from a consumer rather than an endpoint. This runs outside any request, so
        // ICurrentUser resolves to the system user and the entry is recorded against `system` —
        // which is correct: nobody at a keyboard reduced this balance, an approved payment did. The
        // payment's own audit entry names the clerk who took it, and the two are tied together by
        // the reference on this one.
        return SettleAsync(billId, input.Amount, input.Reason, AuditActions.BillPaymentRecorded, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Bill> RecordDepositAsync(Guid billId, RecordBillDepositInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Same settlement, different fact. The action name is what makes "was this bill settled with
        // cash or out of the deposit" a filter on the trail rather than a diff somebody has to read,
        // and the rep who decided to spend the deposit is named on the deposit ledger's own entry.
        return SettleAsync(billId, input.Amount, input.Reason, AuditActions.BillDepositApplied, cancellationToken);
    }

    /// <summary>
    /// Puts money against a bill and audits it under <paramref name="action"/>.
    /// </summary>
    /// <remarks>
    /// Shared by the two things that settle a bill — an approved payment and an applied deposit —
    /// because what settling means is <see cref="Bill.RecordPayment"/>'s answer and there must not
    /// be two of them. What differs is only which fact the trail records.
    /// </remarks>
    private Task<Bill> SettleAsync(Guid billId, decimal amount, string? reason, string action, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                // With its adjustments, like every other write path — Bill.Balance is what a payment
                // is measured against, and since WP-2.4 that is the printed total plus every
                // correction since. A bill loaded without them would be a bill whose balance the
                // aggregate could not vouch for.
                var bill = await LoadAsync(billId, ct).ConfigureAwait(false);
                var before = BillSnapshot.Of(bill);

                bill.RecordPayment(amount, RegistryActor.Of(currentUser), now, reason);

                audit.Record(action, AuditEntityTypes.Bill, bill.Id.ToString(), before, BillSnapshot.Of(bill));

                // No event. Finance already heard the fact from the module that stated it —
                // PaymentApproved for cash, CustomerDepositApplied for a deposit — and a second
                // event saying the same money moved is how a ledger gets a duplicate entry.
                return bill;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<OverdueReviewResult> ReviewOverdueAsync(OverdueReviewInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();
                var asOf = input.AsOf ?? DateOnly.FromDateTime(now.UtcDateTime);
                var actor = RegistryActor.Of(currentUser);

                // Filtered in the database on the two things it can decide — still owed, and past
                // the date — so a review does not walk the whole register. Whether each one really
                // moves is Bill.MarkOverdue's answer.
                var candidates = await database.Bills
                    .Where(bill => bill.Status == BillStatus.Issued || bill.Status == BillStatus.PartiallyPaid)
                    .Where(bill => bill.DueDate != null && bill.DueDate < asOf)
                    .OrderBy(bill => bill.Id)
                    .Take(MaxReviewSize)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                var moved = candidates.Where(bill => bill.MarkOverdue(asOf, actor, now)).ToList();

                var result = new OverdueReviewResult(asOf, moved);

                // One entry for the review, for the reason a billing run gets one: it is one act,
                // and an entry per bill would bury the question "who ran the review and what did it
                // find" under fifty rows saying the same thing.
                audit.Record(
                    AuditActions.BillOverdueReviewed,
                    AuditEntityTypes.BillOverdueReview,
                    asOf.ToString("yyyy-MM-dd"),
                    before: null,
                    after: OverdueReviewSnapshot.Of(result));

                return result;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Bill>> ListAsync(BillQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var bills = database.Bills.AsNoTracking();

        // Matched against non-nullable locals: the columns are stored by name, and EF cannot
        // translate a nullable-to-converted-value comparison.
        if (query.ServiceAccountId is { } account)
        {
            bills = bills.Where(bill => bill.ServiceAccountId == account);
        }

        if (query.CustomerId is { } customer)
        {
            bills = bills.Where(bill => bill.CustomerId == customer);
        }

        if (query.Status is { } status)
        {
            bills = bills.Where(bill => bill.Status == status);
        }

        if (query.OutstandingOnly is true)
        {
            bills = bills.Where(bill =>
                bill.Status == BillStatus.Issued
                || bill.Status == BillStatus.PartiallyPaid
                || bill.Status == BillStatus.Overdue);
        }

        if (!string.IsNullOrWhiteSpace(query.CycleCode))
        {
            var cycle = query.CycleCode.Trim();

            bills = bills.Where(bill => bill.CycleCode == cycle);
        }

        // Adjustments only when they were asked for; lines never. Both are collections a register
        // page would carry and not render, and the flag exists for the one caller whose window is
        // already small and whose subject IS the corrections (WP-2.10's timeline).
        if (query.IncludeAdjustments)
        {
            bills = bills.Include(bill => bill.Adjustments.OrderBy(adjustment => adjustment.Sequence));
        }

        // Ordered by key: ids are Guid v7, so the primary-key index already orders chronologically
        // on Postgres and on the fast tier's SQLite alike.
        return await bills
            .OrderByDescending(bill => bill.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Bill?> FindAsync(Guid billId, CancellationToken cancellationToken = default) =>
        database.Bills
            .AsNoTracking()
            .Include(bill => bill.Lines.OrderBy(line => line.Sequence))
            .Include(bill => bill.Adjustments.OrderBy(adjustment => adjustment.Sequence))
            .FirstOrDefaultAsync(bill => bill.Id == billId, cancellationToken);

    /// <summary>
    /// Why this reading cannot be billed, or <see langword="null"/> if it can.
    /// </summary>
    /// <remarks>
    /// Every rule in one place and every one of them answering in words, because the answers are the
    /// run's output rather than its exceptions. Order matters only for readability: a reading that
    /// fails two of these is reported under the first, which is the one nearest the cause.
    /// </remarks>
    private static string? Reject(
        MeterReadingSummary reading,
        IReadOnlyDictionary<Guid, ServiceAccountSummary> openAccounts,
        HashSet<Guid> billed)
    {
        if (reading.IsException)
        {
            // A flagged reading is worked by hand before it becomes a bill — that is what the
            // exception worklist is for. Billing a high-usage reading unseen is how a transposed
            // digit reaches a customer as a demand for four thousand dollars.
            return $"Reading is on the exception worklist ({reading.ExceptionCode})";
        }

        if (reading.Consumption is null)
        {
            return "Reading has no consumption to bill";
        }

        if (reading.PreviousReadingDate is null)
        {
            // Unreachable for a reading with consumption, which is measured against something. Kept
            // because the period is what the tariff version is chosen by, and a bill for a period
            // that does not exist is worse than a bill that was not raised.
            return "Reading covers no measured period";
        }

        if (!openAccounts.TryGetValue(reading.ServiceLocationId, out var account))
        {
            // A metered premise with nobody taking service there. WP-2.1 seeds exactly this case on
            // purpose: metering and billing are separate questions.
            return "No open service account at the premise";
        }

        if (account.ServiceStartedAt is null)
        {
            // Opened but never energised. Nothing was supplied under this account, so the units on
            // the meter are not its units to be charged for.
            return $"Service account {account.AccountNumber} has never been energised";
        }

        if (billed.Contains(account.Id))
        {
            return "Already billed for this cycle";
        }

        return null;
    }

    /// <summary>
    /// One bill, tracked, with everything a write to it needs in hand.
    /// </summary>
    /// <remarks>
    /// The adjustments are not optional here: <see cref="Bill.Adjust"/> refuses to run without its
    /// whole history, because a running total checked against half a collection is worse than no
    /// check at all.
    /// </remarks>
    private async Task<Bill> LoadAsync(Guid billId, CancellationToken cancellationToken) =>
        await database.Bills
            .Include(bill => bill.Lines)
            .Include(bill => bill.Adjustments)
            .FirstOrDefaultAsync(bill => bill.Id == billId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new BillNotFoundException(billId);

    /// <summary>
    /// The fees waiting against each account, oldest first. One query for the whole run rather than
    /// one per account, and tracked so landing one moves it in the same transaction.
    /// </summary>
    /// <remarks>
    /// Ordered by key: ids are Guid v7, so charges reach a bill in the order they were raised — which
    /// is the order a customer reading the document would expect them in.
    /// </remarks>
    private async Task<IReadOnlyDictionary<Guid, List<AccountCharge>>> PendingChargesAsync(
        IEnumerable<ServiceAccountSummary> forAccounts,
        CancellationToken cancellationToken)
    {
        var ids = forAccounts.Select(account => account.Id).Distinct().ToArray();

        var pending = await database.AccountCharges
            .Where(charge => ids.Contains(charge.ServiceAccountId) && charge.Status == AccountChargeStatus.Pending)
            .OrderBy(charge => charge.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return pending
            .GroupBy(charge => charge.ServiceAccountId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    /// <summary>
    /// The tariff code each account bills on, defaulting where nobody has assigned one. One query
    /// for the whole run rather than one per account.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> TariffsAsync(
        IEnumerable<ServiceAccountSummary> forAccounts,
        CancellationToken cancellationToken)
    {
        var ids = forAccounts.Select(account => account.Id).Distinct().ToArray();

        var assigned = await database.AccountRatePlans
            .AsNoTracking()
            .Where(assignment => ids.Contains(assignment.ServiceAccountId))
            .ToDictionaryAsync(assignment => assignment.ServiceAccountId, assignment => assignment.RatePlanCode, cancellationToken)
            .ConfigureAwait(false);

        return ids.ToDictionary(id => id, id => assigned.GetValueOrDefault(id, DefaultRatePlans.DefaultCode));
    }
}

/// <summary>
/// The shape a bill is audited as. A dedicated record rather than the entity, so changing the entity
/// later cannot silently change the meaning of historic entries.
/// </summary>
/// <param name="Id">Which bill.</param>
/// <param name="BillNumber">Its number, so the entry is readable without a second lookup.</param>
/// <param name="ServiceAccountId">The account billed.</param>
/// <param name="AccountNumber">Its number.</param>
/// <param name="Status">Where the bill stands.</param>
/// <param name="PeriodStart">First day of the billed period.</param>
/// <param name="PeriodEnd">Last day of it.</param>
/// <param name="RatePlanCode">The tariff priced against.</param>
/// <param name="RatePlanEffectiveFrom">The version of it — why these rates and not others.</param>
/// <param name="Consumption">Units billed.</param>
/// <param name="TotalAmount">What the bill comes to as printed. Never moves once it is calculated.</param>
/// <param name="FeeAmount">How much of that is fees from the published schedule rather than supply.</param>
/// <param name="AdjustmentTotal">The signed sum of the corrections made to it since.</param>
/// <param name="AmountDue">What is owed today — the printed total plus those corrections.</param>
/// <param name="AmountPaid">How much has been paid.</param>
/// <param name="DueDate">When it falls due.</param>
public sealed record BillSnapshot(
    Guid Id,
    string BillNumber,
    Guid ServiceAccountId,
    string AccountNumber,
    BillStatus Status,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string? RatePlanCode,
    DateOnly? RatePlanEffectiveFrom,
    decimal Consumption,
    decimal TotalAmount,
    decimal FeeAmount,
    decimal AdjustmentTotal,
    decimal AmountDue,
    decimal AmountPaid,
    DateOnly? DueDate)
{
    /// <summary>Takes a snapshot of <paramref name="bill"/> as it stands.</summary>
    public static BillSnapshot Of(Bill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);

        return new BillSnapshot(
            bill.Id,
            bill.BillNumber,
            bill.ServiceAccountId,
            bill.AccountNumber,
            bill.Status,
            bill.PeriodStart,
            bill.PeriodEnd,
            bill.RatePlanCode,
            bill.RatePlanEffectiveFrom,
            bill.Consumption,
            bill.TotalAmount,
            bill.FeeAmount,
            bill.AdjustmentTotal,
            bill.AmountDue,
            bill.AmountPaid,
            bill.DueDate);
    }
}

/// <summary>The shape a billing run is audited as: what was billed, what came to what, and what was not.</summary>
/// <param name="CycleCode">The reading cycle billed.</param>
/// <param name="Raised">How many bills were raised.</param>
/// <param name="TotalBilled">What they come to.</param>
/// <param name="Skipped">How many readings were passed over.</param>
/// <param name="ByReason">How many were passed over for each reason.</param>
public sealed record BillingRunSnapshot(
    string CycleCode,
    int Raised,
    decimal TotalBilled,
    int Skipped,
    IReadOnlyDictionary<string, int> ByReason)
{
    /// <summary>Takes a snapshot of what <paramref name="run"/> produced.</summary>
    public static BillingRunSnapshot Of(BillingRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return new BillingRunSnapshot(run.CycleCode, run.Raised, run.TotalBilled, run.SkippedCount, run.ByReason);
    }
}

/// <summary>The shape an overdue review is audited as.</summary>
/// <param name="AsOf">The day judged against.</param>
/// <param name="MarkedOverdue">How many bills moved.</param>
/// <param name="TotalOverdue">What is now overdue.</param>
/// <param name="BillNumbers">Which bills moved, so the entry names them.</param>
public sealed record OverdueReviewSnapshot(
    DateOnly AsOf,
    int MarkedOverdue,
    decimal TotalOverdue,
    IReadOnlyList<string> BillNumbers)
{
    /// <summary>Takes a snapshot of what <paramref name="review"/> found.</summary>
    public static OverdueReviewSnapshot Of(OverdueReviewResult review)
    {
        ArgumentNullException.ThrowIfNull(review);

        return new OverdueReviewSnapshot(
            review.AsOf,
            review.MarkedOverdue,
            review.TotalOverdue,
            [.. review.Bills.Select(bill => bill.BillNumber)]);
    }
}
