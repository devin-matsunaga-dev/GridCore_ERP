using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Approvals;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Arrangements;

/// <summary>What a caller supplies to propose a payment arrangement.</summary>
/// <param name="ArrearsBalance">
/// What is being arranged. Never more than the account's past-due balance — see
/// <see cref="IPaymentArrangementService.ProposeAsync"/>.
/// </param>
/// <param name="DownPayment">What is taken up front. Zero where nothing is.</param>
/// <param name="InstalmentCount">How many instalments the rest is spread over.</param>
/// <param name="FirstInstalmentDue">
/// The day the first instalment falls due. A month after the arrangement when the caller does not
/// say.
/// </param>
/// <param name="IntervalDays">Days between instalments after the first.</param>
/// <param name="ArrangedOn">The day it is made. Today when the caller does not say.</param>
/// <param name="Notes">What the desk wants to add — who rang, what was agreed.</param>
public sealed record ProposeArrangementInput(
    decimal ArrearsBalance,
    decimal DownPayment = Money.Zero,
    int InstalmentCount = 3,
    DateOnly? FirstInstalmentDue = null,
    int IntervalDays = ArrangementSchedule.DefaultIntervalDays,
    DateOnly? ArrangedOn = null,
    string? Notes = null);

/// <summary>What a caller supplies to review the register.</summary>
/// <param name="AsOf">
/// The day to judge against. Today when the caller does not say. Its own field so a run missed on
/// Friday can be re-done for Friday, and so a test is not at the mercy of the calendar.
/// </param>
/// <param name="ServiceAccountId">One account only, where a rep is putting one right. The whole register when null.</param>
public sealed record ReviewArrangementsInput(DateOnly? AsOf = null, Guid? ServiceAccountId = null);

/// <summary>One arrangement the review moved, and what it moved it to.</summary>
/// <param name="Arrangement">The arrangement.</param>
/// <param name="From">Where it stood before.</param>
/// <param name="To">Where it stands now.</param>
public sealed record ArrangementReviewChange(
    PaymentArrangement Arrangement,
    PaymentArrangementStatus From,
    PaymentArrangementStatus To);

/// <summary>What one review run did.</summary>
/// <param name="AsOf">The day it judged against.</param>
/// <param name="Reviewed">How many active arrangements it considered.</param>
/// <param name="Changes">Every arrangement it moved, in the order it moved them.</param>
public sealed record ArrangementReviewResult(DateOnly AsOf, int Reviewed, IReadOnlyList<ArrangementReviewChange> Changes)
{
    /// <summary>How many it broke.</summary>
    public int BrokenCount => Changes.Count(change => change.To is PaymentArrangementStatus.Broken);

    /// <summary>How many it recorded as kept.</summary>
    public int KeptCount => Changes.Count(change => change.To is PaymentArrangementStatus.Kept);
}

/// <summary>What a settled payment did to an arrangement.</summary>
/// <param name="Arrangement">The arrangement it was applied to.</param>
/// <param name="AppliedAmount">How much of the payment the schedule took.</param>
/// <param name="SettledSequences">The instalments it settled outright, in order.</param>
/// <param name="IsKept">Whether it was the payment that completed the promise.</param>
public sealed record ArrangementSettlement(
    PaymentArrangement Arrangement,
    decimal AppliedAmount,
    IReadOnlyList<int> SettledSequences,
    bool IsKept);

/// <summary>Payment arrangements: what Customer Service does instead of disconnecting (WP-2.20).</summary>
public interface IPaymentArrangementService
{
    /// <summary>
    /// Proposes an arrangement against an account's past-due balance, raising an approval request
    /// where it goes beyond what the rep may agree alone.
    /// </summary>
    /// <exception cref="RegistryPermissionException">The caller may not arrange payment.</exception>
    /// <exception cref="ServiceAccountNotFoundException">There is no such service account.</exception>
    /// <exception cref="RegistryValidationException">The figures do not describe a schedule anybody could keep.</exception>
    /// <exception cref="RegistryWorkflowException">More than the arrears, or an arrangement already stands.</exception>
    Task<PaymentArrangement> ProposeAsync(
        Guid serviceAccountId,
        ProposeArrangementInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings a proposed arrangement into force. <b>Refused while an approval it needs is
    /// undecided</b>, which is what "requires approval before it becomes active" means.
    /// </summary>
    /// <exception cref="RegistryPermissionException">The caller may not arrange payment.</exception>
    /// <exception cref="PaymentArrangementNotFoundException">There is no such arrangement.</exception>
    /// <exception cref="RegistryWorkflowException">It is not a proposal, or its approval has not been granted.</exception>
    Task<PaymentArrangement> ActivateAsync(Guid arrangementId, CancellationToken cancellationToken = default);

    /// <summary>Every arrangement against one account, newest first. A read.</summary>
    Task<IReadOnlyList<PaymentArrangement>> ListForAccountAsync(
        Guid serviceAccountId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>The published ceilings, as they stand.</summary>
    Task<IReadOnlyList<ArrangementLimit>> LimitsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes down where every active arrangement now stands: breaking those that have missed an
    /// instalment and recording as kept those that have been paid off.
    /// </summary>
    /// <exception cref="RegistryPermissionException">The caller may not arrange payment.</exception>
    Task<ArrangementReviewResult> ReviewAsync(
        ReviewArrangementsInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies money that arrived on an account to the arrangement standing against it, earliest
    /// unpaid instalment first.
    /// </summary>
    /// <remarks>
    /// Reached only by the consumer of <c>PaymentApproved</c>, which runs as the system — so it takes
    /// no permission gate. A rep does not settle an instalment; they take a payment, and Payments
    /// says so.
    /// </remarks>
    /// <returns>What the payment did, or <see langword="null"/> where no arrangement was standing.</returns>
    Task<ArrangementSettlement?> RecordPaymentAsync(
        Guid serviceAccountId,
        decimal amount,
        Guid paymentId,
        string providerReference,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Payment arrangements over the customers schema (WP-2.20).
/// </summary>
/// <remarks>
/// <para>
/// <b>It never writes to Billing and never publishes an event.</b> An arrangement is a promise about
/// receivables that already exist: the bills say what is owed, this says when it will arrive, and a
/// register that touched a bill's status would be a second opinion about a debt. Nothing downstream
/// consumes an arrangement either — WP-2.21 reads it through this module — and an event nobody
/// consumes is an instruction rather than a fact (the rule WP-2.15, WP-2.16 and WP-2.19 all
/// followed).
/// </para>
/// <para>
/// <b>The arrears ceiling is read through <see cref="IBillDirectory"/>, and the arranged figure is
/// stamped.</b> Billing owns the register and this module may not read <c>billing.bills</c>; what is
/// asked is one question — what is past due today — and the answer is written onto the arrangement
/// rather than re-read, exactly as a dunning notice stamps what the customer was told.
/// </para>
/// <para>
/// <b>Over the rep's limit, the approval primitive decides it — not a second bespoke workflow.</b>
/// WORK_PACKAGES.md asks for exactly that, and this is <see cref="IApprovalService"/>'s first module
/// consumer: WP-0.4 built the queue, and until now every module that might have used one had a
/// gate rather than a decision. The request names <c>customers.arrange</c> as what a decider must
/// hold, which — with <c>platform.approve</c>, which the primitive demands as well — is a Manager
/// and not the rep who raised it.
/// </para>
/// </remarks>
public sealed class PaymentArrangementService(
    CustomersDbContext database,
    IBillDirectory bills,
    IApprovalService approvals,
    IRegistryNumberGenerator numbers,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider clock) : IPaymentArrangementService
{
    /// <summary>The kind of decision an over-limit arrangement raises, as the approval queue files it.</summary>
    public const string ApprovalRequestType = "customers.payment_arrangement";

    /// <summary>The most arrangements one read will return for an account.</summary>
    public const int MaxArrangements = 100;

    /// <summary>Most arrangements one review will consider in a pass.</summary>
    /// <remarks>
    /// Generous rather than tight, the call <c>LateChargeService.MaxRunSize</c> makes about the run
    /// beside it: this exists so a data fault cannot turn one job into an unbounded read.
    /// </remarks>
    public const int MaxReviewSize = 500;

    /// <inheritdoc />
    public Task<PaymentArrangement> ProposeAsync(
        Guid serviceAccountId,
        ProposeArrangementInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                // DEMANDED BEFORE ANYTHING IS READ, the call WP-2.19's evaluation makes: an
                // arrangement decides what a customer will be held to and suppresses a disconnection
                // while it stands, so the refusal must not depend on whether the account happened to
                // be in arrears.
                RequireArrangePermission();

                var now = clock.GetUtcNow();
                var arrangedOn = input.ArrangedOn ?? DateOnly.FromDateTime(now.UtcDateTime);

                var (account, customer) = await LoadAsync(serviceAccountId, ct).ConfigureAwait(false);

                // ONE PROMISE AT A TIME. A second arrangement standing beside the first would mean
                // two answers to "what has this customer agreed to pay", and the protection the
                // account enjoys would depend on which was read.
                var standing = await StandingArrangementAsync(account.Id, arrangedOn, ct).ConfigureAwait(false);

                if (standing is not null)
                {
                    throw new RegistryWorkflowException(
                        $"{account.AccountNumber} already has arrangement {standing.ArrangementNumber}, which is "
                        + $"{standing.StandingOn(arrangedOn)}. Settle or review it before making another.");
                }

                var arrears = await bills.ArrearsForAccountAsync(account.Id, arrangedOn, ct).ConfigureAwait(false);

                // REFUSED ABOVE THE ARREARS. An arrangement is a promise about receivables that
                // already exist, so promising more than exists would be the utility inventing a
                // debt — and the past-due figure is the one that matters, because a bill issued last
                // week and due next month is not something anybody is behind with. A 409 rather than
                // a 400: the request is well formed and the register is simply not in a state that
                // allows it.
                if (input.ArrearsBalance > arrears.PastDueAmount)
                {
                    throw new RegistryWorkflowException(
                        $"{account.AccountNumber} has {arrears.PastDueAmount:0.00} past due, and an arrangement cannot "
                        + $"promise the {input.ArrearsBalance:0.00} asked for. An arrangement records how an existing "
                        + "debt will be paid; it never creates one.");
                }

                var limit = await LimitForAsync(customer.Class, ct).ConfigureAwait(false);

                var schedule = ArrangementSchedule.Build(
                    input.ArrearsBalance,
                    input.DownPayment,
                    input.InstalmentCount,
                    arrangedOn,
                    input.FirstInstalmentDue ?? arrangedOn.AddDays(input.IntervalDays),
                    input.IntervalDays);

                // WP-0.4's primitive rather than a second approval table — WORK_PACKAGES.md's own
                // instruction. Raised inside this unit of work, so an arrangement and the request
                // that has to decide it commit together or neither does: a proposal nobody could
                // approve, or an approval pointing at nothing, are both worse than a failed call.
                var approval = limit.RequiresApproval(input.ArrearsBalance, input.InstalmentCount)
                    ? await RaiseApprovalAsync(account, customer, input, limit, ct).ConfigureAwait(false)
                    : null;

                var arrangement = PaymentArrangement.Propose(
                    await numbers.NextPaymentArrangementNumberAsync(ct).ConfigureAwait(false),
                    account,
                    customer,
                    input.ArrearsBalance,
                    arrears.Currency,
                    input.DownPayment,
                    input.InstalmentCount,
                    input.IntervalDays,
                    arrangedOn,
                    limit,
                    approval?.Id,
                    schedule,
                    input.Notes,
                    RegistryActor.Of(currentUser),
                    now);

                database.PaymentArrangements.Add(arrangement);

                // INVARIANT 1. The snapshot carries the schedule, because "what did this customer
                // actually agree to" is the question somebody asks when it is disputed — and the
                // instalment rows will by then have been paid against.
                audit.Record(
                    AuditActions.PaymentArrangementProposed,
                    AuditEntityTypes.PaymentArrangement,
                    arrangement.Id.ToString(),
                    before: null,
                    after: PaymentArrangementSnapshot.Of(arrangement));

                return arrangement;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<PaymentArrangement> ActivateAsync(Guid arrangementId, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                RequireArrangePermission();

                var now = clock.GetUtcNow();
                var today = DateOnly.FromDateTime(now.UtcDateTime);

                var arrangement = await TrackedAsync(arrangementId, ct).ConfigureAwait(false);
                var before = PaymentArrangementSnapshot.Of(arrangement);

                // THE APPROVAL GATE. WORK_PACKAGES.md: "an arrangement beyond the rep's limit
                // requires approval before it becomes active". The status is read from the queue at
                // the moment of activation rather than mirrored onto this row when it was decided —
                // a mirrored copy would be a second answer that could go stale, and the queue is the
                // register that owns the decision.
                if (arrangement.ApprovalRequestId is { } approvalId)
                {
                    var approval = await approvals.FindAsync(approvalId, ct).ConfigureAwait(false)
                        ?? throw new RegistryWorkflowException(
                            $"{arrangement.ArrangementNumber} was raised for approval as request {approvalId}, and that "
                            + "request cannot be found. It cannot be brought into force without the decision it needs.");

                    if (approval.Status is not ApprovalStatus.Approved)
                    {
                        throw new RegistryWorkflowException(
                            $"{arrangement.ArrangementNumber} is {arrangement.ArrearsBalance:0.00} over "
                            + $"{arrangement.InstalmentCount} instalments, beyond the "
                            + $"{arrangement.LimitMaximumBalance:0.00} over {arrangement.LimitMaximumInstalments} a "
                            + $"representative may agree alone. Its approval request is {approval.Status}, so it cannot "
                            + "be brought into force.");
                    }
                }

                arrangement.Activate(today);

                audit.Record(
                    AuditActions.PaymentArrangementActivated,
                    AuditEntityTypes.PaymentArrangement,
                    arrangement.Id.ToString(),
                    before,
                    PaymentArrangementSnapshot.Of(arrangement));

                return arrangement;
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentArrangement>> ListForAccountAsync(
        Guid serviceAccountId,
        int limit,
        CancellationToken cancellationToken = default) =>
        await ReadableArrangements()
            .Where(arrangement => arrangement.ServiceAccountId == serviceAccountId)

            // Ordered by key: ids are Guid v7, so the primary-key index already orders
            // chronologically on Postgres and on the fast tier's SQLite alike.
            .OrderByDescending(arrangement => arrangement.Id)
            .Take(Math.Clamp(limit, 1, MaxArrangements))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArrangementLimit>> LimitsAsync(CancellationToken cancellationToken = default) =>
        await database.ArrangementLimits
            .AsNoTracking()
            .OrderBy(limit => limit.CustomerClass)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<ArrangementReviewResult> ReviewAsync(
        ReviewArrangementsInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                RequireArrangePermission();

                var now = clock.GetUtcNow();
                var asOf = input.AsOf ?? DateOnly.FromDateTime(now.UtcDateTime);

                var query = database.PaymentArrangements
                    .Include(arrangement => arrangement.Instalments)
                    .Where(arrangement => arrangement.Status == PaymentArrangementStatus.Active);

                if (input.ServiceAccountId is { } accountId)
                {
                    query = query.Where(arrangement => arrangement.ServiceAccountId == accountId);
                }

                var active = await query
                    .OrderBy(arrangement => arrangement.Id)
                    .Take(MaxReviewSize)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                var changes = new List<ArrangementReviewChange>();

                foreach (var arrangement in active)
                {
                    // THE SAME PURE FUNCTION THE DISCONNECTION SEAM READS. The run does not decide
                    // anything the directory would decide differently; it writes down what is
                    // already true, which is what stops the stored status and the computed one ever
                    // disagreeing.
                    var standing = arrangement.StandingOn(asOf);

                    if (standing == PaymentArrangementStatus.Active)
                    {
                        continue;
                    }

                    var before = PaymentArrangementSnapshot.Of(arrangement);

                    if (standing is PaymentArrangementStatus.Kept)
                    {
                        arrangement.Keep(asOf);
                    }
                    else
                    {
                        arrangement.Break(asOf);
                    }

                    changes.Add(new ArrangementReviewChange(arrangement, PaymentArrangementStatus.Active, standing));

                    // One entry per arrangement moved, on top of the run's own below: a broken
                    // arrangement restores disconnection eligibility, so "when did this account stop
                    // being protected, and on what" has to be answerable from the trail about that
                    // account rather than from a run that touched four hundred others.
                    audit.Record(
                        standing is PaymentArrangementStatus.Kept
                            ? AuditActions.PaymentArrangementKept
                            : AuditActions.PaymentArrangementBroken,
                        AuditEntityTypes.PaymentArrangement,
                        arrangement.Id.ToString(),
                        before,
                        PaymentArrangementSnapshot.Of(arrangement));
                }

                var result = new ArrangementReviewResult(asOf, active.Count, changes);

                audit.Record(
                    AuditActions.PaymentArrangementReviewRun,
                    AuditEntityTypes.PaymentArrangement,

                    // The run is about the register rather than about a row, so the entity id is the
                    // scope it ran over — the call LateChargeRun already made.
                    input.ServiceAccountId?.ToString() ?? "*",
                    before: null,
                    after: ArrangementReviewSnapshot.Of(result));

                return result;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ArrangementSettlement?> RecordPaymentAsync(
        Guid serviceAccountId,
        decimal amount,
        Guid paymentId,
        string providerReference,
        CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                if (amount <= Money.Zero)
                {
                    return null;
                }

                var now = clock.GetUtcNow();
                var today = DateOnly.FromDateTime(now.UtcDateTime);

                // Only an arrangement actually IN FORCE takes a payment. Money arriving against a
                // proposal nobody activated is money against the bills, and crediting it to a
                // schedule the customer never agreed to would show a promise being kept that was
                // never made.
                var arrangement = await database.PaymentArrangements
                    .Include(candidate => candidate.Instalments)
                    .Where(candidate =>
                        candidate.ServiceAccountId == serviceAccountId
                        && candidate.Status == PaymentArrangementStatus.Active)
                    .OrderByDescending(candidate => candidate.Id)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                if (arrangement is null)
                {
                    return null;
                }

                var before = PaymentArrangementSnapshot.Of(arrangement);
                var applied = arrangement.Apply(amount, now);

                if (applied.Count is 0)
                {
                    return null;
                }

                // Kept the moment the last instalment lands, rather than waiting for the next review
                // run: the customer has finished paying, and an account still showing an open
                // promise would have a rep chasing somebody who owes nothing.
                var isKept = arrangement.OutstandingAmount <= Money.Zero;

                if (isKept)
                {
                    arrangement.Keep(today);
                }

                var settlement = new ArrangementSettlement(
                    arrangement,
                    Money.Total(applied.Select(line => line.Applied)),
                    [.. applied.Where(line => line.Instalment.IsSettled).Select(line => line.Instalment.Sequence)],
                    isKept);

                // Recorded against `system`: this happens in a consumer rather than at somebody's
                // keyboard, and the clerk who took the money is named on the payment's own entry.
                audit.Record(
                    AuditActions.PaymentArrangementPaymentApplied,
                    AuditEntityTypes.PaymentArrangement,
                    arrangement.Id.ToString(),
                    before,
                    ArrangementSettlementSnapshot.Of(settlement, paymentId, providerReference));

                return settlement;
            },
            cancellationToken);

    /// <summary>
    /// The arrangement standing against <paramref name="serviceAccountId"/> on
    /// <paramref name="asOf"/>, or <see langword="null"/> where none is.
    /// </summary>
    /// <remarks>
    /// <b>"Standing" means proposed or in force</b>, not "the most recent". A kept arrangement is
    /// history and a broken one is the case disconnection exists for; neither stops the desk making
    /// a fresh promise, which is the whole of "replaced, never resumed". A proposal counts because a
    /// second one raised beside it would leave the desk with two schedules and one telephone call.
    /// </remarks>
    private async Task<PaymentArrangement?> StandingArrangementAsync(
        Guid serviceAccountId,
        DateOnly asOf,
        CancellationToken cancellationToken)
    {
        var open = await ReadableArrangements()
            .Where(arrangement =>
                arrangement.ServiceAccountId == serviceAccountId
                && (arrangement.Status == PaymentArrangementStatus.Proposed
                    || arrangement.Status == PaymentArrangementStatus.Active))
            .OrderByDescending(arrangement => arrangement.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // An active arrangement that has quietly defaulted is NOT standing: it is broken in every
        // sense but the one the review run has not written down yet, and refusing a fresh promise
        // because of it would leave a customer who missed one instalment unable to make another
        // arrangement until a job ran.
        return open.Find(arrangement =>
            arrangement.StandingOn(asOf) is PaymentArrangementStatus.Proposed or PaymentArrangementStatus.Active);
    }

    /// <summary>Raises the approval an over-limit arrangement needs before it may take effect.</summary>
    private Task<ApprovalRequest> RaiseApprovalAsync(
        ServiceAccount account,
        Customer customer,
        ProposeArrangementInput input,
        ArrangementLimit limit,
        CancellationToken cancellationToken) =>
        approvals.RequestAsync(
            new ApprovalRequestInput(
                ApprovalRequestType,
                AuditEntityTypes.ServiceAccount,
                account.Id.ToString(),

                // What a DECIDER must hold, on top of platform.approve which the primitive demands
                // of everybody. Naming customers.arrange rather than a supervisor role is what keeps
                // the primitive reusable: a utility that decides arrangements need a different
                // signature re-cuts one line of RolePermissionMap.
                Permissions.Customers.Arrange,
                new
                {
                    account.AccountNumber,
                    Customer = customer.Name,
                    CustomerClass = customer.Class.ToString(),
                    input.ArrearsBalance,
                    input.DownPayment,
                    input.InstalmentCount,
                    input.IntervalDays,
                    LimitBalance = limit.MaximumBalance,
                    LimitInstalments = limit.MaximumInstalments,
                },
                $"{input.ArrearsBalance:0.00} over {input.InstalmentCount} instalments for {account.AccountNumber} is "
                + $"beyond the {limit.MaximumBalance:0.00} over {limit.MaximumInstalments} a representative may agree "
                + $"for a {customer.Class} customer."),
            cancellationToken);

    /// <summary>The limit governing <paramref name="customerClass"/>, read from the table rather than the static list.</summary>
    /// <remarks>
    /// The same call <c>DepositRuleService</c>, <c>FeeScheduleService</c> and <c>DelinquencyService</c>
    /// make: the static list is how the rows are <i>seeded</i>, and what is in force is whatever the
    /// database holds — so raising a ceiling is a migration and not a redeploy of the domain.
    /// </remarks>
    /// <exception cref="RegistryValidationException">The table publishes none for that class.</exception>
    private async Task<ArrangementLimit> LimitForAsync(CustomerClass customerClass, CancellationToken cancellationToken) =>
        await database.ArrangementLimits
            .AsNoTracking()
            .FirstOrDefaultAsync(limit => limit.CustomerClass == customerClass, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new RegistryValidationException(
            $"No arrangement limit is published for {customerClass}, so no arrangement for one can be judged against a "
            + "representative's authority. The limits are reference data: add the row in a migration.");

    /// <summary>The read shape: untracked, with the schedule, ordered so a screen never has to sort it.</summary>
    private IQueryable<PaymentArrangement> ReadableArrangements() =>
        database.PaymentArrangements
            .AsNoTracking()
            .Include(arrangement => arrangement.Instalments.OrderBy(instalment => instalment.Sequence));

    /// <summary>Loads one arrangement for writing, with its schedule.</summary>
    /// <exception cref="PaymentArrangementNotFoundException">There is no such arrangement.</exception>
    private async Task<PaymentArrangement> TrackedAsync(Guid arrangementId, CancellationToken cancellationToken) =>
        await database.PaymentArrangements
            .Include(arrangement => arrangement.Instalments)
            .FirstOrDefaultAsync(arrangement => arrangement.Id == arrangementId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new PaymentArrangementNotFoundException(arrangementId);

    /// <summary>Loads the account and the customer who holds it.</summary>
    /// <exception cref="ServiceAccountNotFoundException">There is no such account.</exception>
    /// <exception cref="CustomerNotFoundException">Its customer has gone, which cannot happen.</exception>
    private async Task<(ServiceAccount Account, Customer Customer)> LoadAsync(
        Guid serviceAccountId,
        CancellationToken cancellationToken)
    {
        var account = await database.ServiceAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == serviceAccountId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ServiceAccountNotFoundException(serviceAccountId);

        // Untracked: nothing here writes to the customer. An arrangement moves no money, so it never
        // touches DepositHeld — the one column on that row this module's other services do move.
        var customer = await database.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == account.CustomerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CustomerNotFoundException(account.CustomerId);

        return (account, customer);
    }

    /// <exception cref="RegistryPermissionException">The caller does not hold <c>customers.arrange</c>.</exception>
    private void RequireArrangePermission()
    {
        if (currentUser.HasPermission(Permissions.Customers.Arrange))
        {
            return;
        }

        throw new RegistryPermissionException(
            $"Making or moving a payment arrangement commits the utility to accept a debt in instalments and suppresses "
            + $"disconnection while it stands, so it requires the '{Permissions.Customers.Arrange}' permission. Reading "
            + "what has been arranged is a read and needs no such grant.");
    }
}

/// <summary>The shape one instalment is audited as.</summary>
/// <param name="Sequence">Where it falls in the schedule.</param>
/// <param name="DueDate">The day it falls due.</param>
/// <param name="Amount">What was promised on it.</param>
/// <param name="PaidAmount">How much has arrived.</param>
/// <param name="IsDownPayment">Whether it is the money taken up front.</param>
public sealed record ArrangementInstalmentSnapshot(
    int Sequence,
    DateOnly DueDate,
    decimal Amount,
    decimal PaidAmount,
    bool IsDownPayment)
{
    /// <summary>Takes a snapshot of <paramref name="instalment"/>.</summary>
    public static ArrangementInstalmentSnapshot Of(ArrangementInstalment instalment)
    {
        ArgumentNullException.ThrowIfNull(instalment);

        return new ArrangementInstalmentSnapshot(
            instalment.Sequence,
            instalment.DueDate,
            instalment.Amount,
            instalment.PaidAmount,
            instalment.IsDownPayment);
    }
}

/// <summary>The shape an arrangement is audited as — the promise, in full.</summary>
/// <param name="Id">Which arrangement.</param>
/// <param name="ArrangementNumber">Its number, so the entry reads without a second lookup.</param>
/// <param name="ServiceAccountId">The account it is against.</param>
/// <param name="AccountNumber">Its number.</param>
/// <param name="Status">Where it stands, as recorded.</param>
/// <param name="Currency">ISO 4217 code every figure is in.</param>
/// <param name="ArrearsBalance">What was arranged.</param>
/// <param name="DownPayment">What was taken up front.</param>
/// <param name="InstalmentCount">How many instalments the rest was spread over.</param>
/// <param name="PaidAmount">What has arrived.</param>
/// <param name="OutstandingAmount">What is still promised.</param>
/// <param name="LimitMaximumBalance">The ceiling that governed it.</param>
/// <param name="LimitMaximumInstalments">The instalment ceiling that governed it.</param>
/// <param name="RequiresApproval">Whether it went beyond one of them.</param>
/// <param name="ApprovalRequestId">The request raised to decide it.</param>
/// <param name="ActivatedOn">The day it came into force.</param>
/// <param name="ClosedOn">The day it was kept or broken.</param>
/// <param name="Instalments">The schedule, in order.</param>
public sealed record PaymentArrangementSnapshot(
    Guid Id,
    string ArrangementNumber,
    Guid ServiceAccountId,
    string AccountNumber,
    PaymentArrangementStatus Status,
    string Currency,
    decimal ArrearsBalance,
    decimal DownPayment,
    int InstalmentCount,
    decimal PaidAmount,
    decimal OutstandingAmount,
    decimal LimitMaximumBalance,
    int LimitMaximumInstalments,
    bool RequiresApproval,
    Guid? ApprovalRequestId,
    DateOnly? ActivatedOn,
    DateOnly? ClosedOn,
    IReadOnlyList<ArrangementInstalmentSnapshot> Instalments)
{
    /// <summary>Takes a snapshot of <paramref name="arrangement"/>.</summary>
    public static PaymentArrangementSnapshot Of(PaymentArrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);

        return new PaymentArrangementSnapshot(
            arrangement.Id,
            arrangement.ArrangementNumber,
            arrangement.ServiceAccountId,
            arrangement.AccountNumber,
            arrangement.Status,
            arrangement.Currency,
            arrangement.ArrearsBalance,
            arrangement.DownPayment,
            arrangement.InstalmentCount,
            arrangement.PaidAmount,
            arrangement.OutstandingAmount,
            arrangement.LimitMaximumBalance,
            arrangement.LimitMaximumInstalments,
            arrangement.RequiresApproval,
            arrangement.ApprovalRequestId,
            arrangement.ActivatedOn,
            arrangement.ClosedOn,
            [.. arrangement.Instalments.OrderBy(instalment => instalment.Sequence).Select(ArrangementInstalmentSnapshot.Of)]);
    }
}

/// <summary>The shape a settled payment is audited as.</summary>
/// <param name="ArrangementNumber">The arrangement it landed on.</param>
/// <param name="PaymentId">The payment in Payments' schema.</param>
/// <param name="ProviderReference">The provider's own reference, for reconciliation.</param>
/// <param name="AppliedAmount">How much of it the schedule took.</param>
/// <param name="SettledSequences">The instalments it settled outright.</param>
/// <param name="OutstandingAmount">What is still promised afterwards.</param>
/// <param name="IsKept">Whether it completed the promise.</param>
public sealed record ArrangementSettlementSnapshot(
    string ArrangementNumber,
    Guid PaymentId,
    string ProviderReference,
    decimal AppliedAmount,
    IReadOnlyList<int> SettledSequences,
    decimal OutstandingAmount,
    bool IsKept)
{
    /// <summary>Takes a snapshot of <paramref name="settlement"/>.</summary>
    public static ArrangementSettlementSnapshot Of(
        ArrangementSettlement settlement,
        Guid paymentId,
        string providerReference)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        return new ArrangementSettlementSnapshot(
            settlement.Arrangement.ArrangementNumber,
            paymentId,
            providerReference,
            settlement.AppliedAmount,
            settlement.SettledSequences,
            settlement.Arrangement.OutstandingAmount,
            settlement.IsKept);
    }
}

/// <summary>The shape a review run is audited as.</summary>
/// <param name="AsOf">The day it judged against.</param>
/// <param name="Reviewed">How many active arrangements it considered.</param>
/// <param name="BrokenCount">How many it broke.</param>
/// <param name="KeptCount">How many it recorded as kept.</param>
/// <param name="BrokenArrangements">The numbers it broke, so the entry reads without a second lookup.</param>
public sealed record ArrangementReviewSnapshot(
    DateOnly AsOf,
    int Reviewed,
    int BrokenCount,
    int KeptCount,
    IReadOnlyList<string> BrokenArrangements)
{
    /// <summary>Takes a snapshot of <paramref name="result"/>.</summary>
    public static ArrangementReviewSnapshot Of(ArrangementReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ArrangementReviewSnapshot(
            result.AsOf,
            result.Reviewed,
            result.BrokenCount,
            result.KeptCount,
            [
                .. result.Changes
                    .Where(change => change.To is PaymentArrangementStatus.Broken)
                    .Select(change => change.Arrangement.ArrangementNumber),
            ]);
    }
}
