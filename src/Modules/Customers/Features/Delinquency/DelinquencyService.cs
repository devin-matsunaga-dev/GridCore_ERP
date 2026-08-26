using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Delinquency;

/// <summary>What a caller supplies to record that a dunning notice was served.</summary>
/// <param name="NoticeType">Which notice went out.</param>
/// <param name="ServedOn">The day it went out. Today when the caller does not say.</param>
/// <param name="Notes">What the desk wants to add — how it was served, who took the call.</param>
public sealed record ServeNoticeInput(DunningNoticeType NoticeType, DateOnly? ServedOn = null, string? Notes = null);

/// <summary>What a caller supplies to evaluate an account for disconnection.</summary>
/// <param name="AsOf">
/// The day to judge against. Today when the caller does not say — and a caller almost never should
/// say, because this evaluation <i>moves money</i> and back-dating one would apply a deposit against
/// arrears as they stood on a day that has passed.
/// </param>
public sealed record EvaluateDisconnectionInput(DateOnly? AsOf = null);

/// <summary>
/// What one account's delinquency looks like: what is owed, what has been served, and where it
/// stands against the four disconnection tests.
/// </summary>
/// <param name="ServiceAccountId">The account.</param>
/// <param name="AccountNumber">Its number, as quoted.</param>
/// <param name="CustomerId">Who holds it.</param>
/// <param name="CustomerName">Their name.</param>
/// <param name="AccountStatus">Where the account stands, by name.</param>
/// <param name="Arrears">What is owed, aged, from Billing's own register.</param>
/// <param name="DepositHeld">What the utility holds against the customer.</param>
/// <param name="Steps">The published dunning sequence, in the order it is served.</param>
/// <param name="DueStep">The furthest step this account has reached, or <see langword="null"/>.</param>
/// <param name="Notices">Every notice served, newest first.</param>
/// <param name="Eligibility">
/// Where the account stands against the four tests, with the deposit offset <b>computed and not
/// made</b> — <c>IsOffsetApplied</c> is false. A screen showing what would happen must not be a
/// screen that makes it happen.
/// </param>
public sealed record DelinquencyPicture(
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    string CustomerName,
    string AccountStatus,
    AccountArrears Arrears,
    decimal DepositHeld,
    IReadOnlyList<DunningStep> Steps,
    DunningStep? DueStep,
    IReadOnlyList<DunningNotice> Notices,
    DisconnectionEligibility Eligibility);

/// <summary>What an evaluation did and what it decided.</summary>
/// <param name="Eligibility">The answer, with the offset now actually made.</param>
/// <param name="OffsetEntries">The deposit movements it wrote, oldest bill first. Empty where nothing qualified.</param>
public sealed record DisconnectionEvaluation(
    DisconnectionEligibility Eligibility,
    IReadOnlyList<DepositEntry> OffsetEntries)
{
    /// <summary>What the offset came to.</summary>
    public decimal OffsetAmount => Money.Total(OffsetEntries.Select(entry => entry.Amount));
}

/// <summary>Delinquency, dunning and the statutory deposit offset (WP-2.19).</summary>
public interface IDelinquencyService
{
    /// <summary>One account's delinquency picture. A read: it moves nothing.</summary>
    /// <exception cref="ServiceAccountNotFoundException">There is no such service account.</exception>
    Task<DelinquencyPicture> GetAsync(
        Guid serviceAccountId,
        DateOnly? asOf = null,
        CancellationToken cancellationToken = default);

    /// <summary>Records that a dunning notice was served over an account.</summary>
    /// <exception cref="ServiceAccountNotFoundException">There is no such service account.</exception>
    /// <exception cref="RegistryValidationException">The notice is not one GridCore publishes.</exception>
    /// <exception cref="RegistryWorkflowException">The account has not reached that step.</exception>
    Task<DunningNotice> ServeAsync(
        Guid serviceAccountId,
        ServeNoticeInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates an account for disconnection, <b>applying the held deposit to qualifying past-due
    /// amounts first</b> as CNMI Public Law 16-17 obliges.
    /// </summary>
    /// <exception cref="RegistryPermissionException">The caller may not move a deposit.</exception>
    /// <exception cref="ServiceAccountNotFoundException">There is no such service account.</exception>
    Task<DisconnectionEvaluation> EvaluateAsync(
        Guid serviceAccountId,
        EvaluateDisconnectionInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Every notice served over one account, newest first.</summary>
    Task<IReadOnlyList<DunningNotice>> ListNoticesAsync(
        Guid serviceAccountId,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Delinquency over the customers schema, the deposit ledger and Billing's arrears seam.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads no bills and computes no ageing.</b> What an account owes and how old the debt is
/// comes from <see cref="IBillDirectory.ArrearsForAccountAsync"/> — Billing owns the register and
/// owns the bands — and this module may not read <c>billing.bills</c>. What it adds is everything
/// the law is about: which notices went out, when, and what that means for a supply.
/// </para>
/// <para>
/// <b>Reading is a read and evaluating is a write, and the split is the whole design.</b>
/// WORK_PACKAGES.md says evaluating eligibility applies the deposit first; a GET that moved money
/// would be a GET that a refresh runs twice. So <see cref="GetAsync"/> answers with the offset
/// <i>computed</i> — the same figures, marked as not applied — and <see cref="EvaluateAsync"/> is a
/// POST that makes it so. WP-2.21's disconnection process consumes the second.
/// </para>
/// <para>
/// <b>The offset goes through <see cref="ICustomerDepositService"/> and never writes a ledger entry
/// itself.</b> That service holds the gate, the audit entry and the <c>CustomerDepositApplied</c>
/// event Finance posts the double entry from and Billing reduces the bill from — a second writer
/// would be a second set of rules about what a deposit movement is. The only thing added here is
/// the reason, which names the statute.
/// </para>
/// <para>
/// <b>An account whose past-due bills belong to a previous holder cannot be offset, and fails
/// loudly.</b> A deposit is held against a customer and a bill is owed by one, so
/// <c>CustomerDepositService</c> refuses to spend one on the other — which is right, and is a case
/// WP-2.24's change of account holder has to design for rather than one to paper over here.
/// </para>
/// </remarks>
public sealed class DelinquencyService(
    CustomersDbContext database,
    IBillDirectory bills,
    ICustomerDepositService deposits,
    IPaymentArrangementDirectory arrangements,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider clock) : IDelinquencyService
{
    /// <summary>The most notices one read will return, whatever an account's history holds.</summary>
    public const int MaxNotices = 100;

    /// <inheritdoc />
    public async Task<DelinquencyPicture> GetAsync(
        Guid serviceAccountId,
        DateOnly? asOf = null,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var on = asOf ?? DateOnly.FromDateTime(now.UtcDateTime);

        var (account, customer) = await LoadAsync(serviceAccountId, cancellationToken).ConfigureAwait(false);

        var arrears = await bills.ArrearsForAccountAsync(serviceAccountId, on, cancellationToken).ConfigureAwait(false);
        var steps = await StepsAsync(cancellationToken).ConfigureAwait(false);
        var notices = await ListNoticesAsync(serviceAccountId, MaxNotices, cancellationToken).ConfigureAwait(false);
        var standing = await arrangements.StandingForAccountAsync(serviceAccountId, cancellationToken).ConfigureAwait(false);

        var eligibility = DisconnectionRules.Decide(
            arrears,
            customer.DepositHeld,
            RequireDisconnectionStep(steps),
            LatestDisconnectionNotice(notices),
            standing,

            // A READ. The figures say what the offset would be; nothing has moved.
            isOffsetApplied: false);

        return new DelinquencyPicture(
            account.Id,
            account.AccountNumber,
            customer.Id,
            customer.Name,
            account.Status.ToString(),
            arrears,
            customer.DepositHeld,
            steps,
            DunningSequence.DueOn(steps, arrears.DaysPastDue, arrears.PastDueAmount),
            notices,
            eligibility);
    }

    /// <inheritdoc />
    public Task<DunningNotice> ServeAsync(
        Guid serviceAccountId,
        ServeNoticeInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();
                var servedOn = input.ServedOn ?? DateOnly.FromDateTime(now.UtcDateTime);

                var (account, customer) = await LoadAsync(serviceAccountId, ct).ConfigureAwait(false);

                var steps = await StepsAsync(ct).ConfigureAwait(false);

                var step = steps.FirstOrDefault(candidate => candidate.NoticeType == input.NoticeType)
                    ?? throw new RegistryValidationException(
                        $"No dunning step is published for {input.NoticeType}. The sequence is reference data: "
                        + "add the row in a migration.");

                // The arrears as it stands on the day the notice went out, not as it stands now: the
                // record has to say what the customer was told.
                var arrears = await bills.ArrearsForAccountAsync(account.Id, servedOn, ct).ConfigureAwait(false);

                // REFUSED WHERE THE ACCOUNT HAS NOT REACHED THE STEP. A disconnection notice served
                // on somebody eleven days late is a notice that starts a statutory clock the utility
                // was not entitled to start — and it is exactly the record that would later be
                // produced to justify cutting them off. A 409 rather than a 400: the request is well
                // formed and the register is simply not in a state that allows it.
                if (!step.IsDue(arrears.DaysPastDue, arrears.PastDueAmount))
                {
                    throw new RegistryWorkflowException(
                        $"{account.AccountNumber} has {arrears.PastDueAmount:0.00} past due, {arrears.DaysPastDue} days late, "
                        + $"and a {step.Name.ToLowerInvariant()} falls due at {step.MinimumArrears:0.00} and "
                        + $"{step.DaysPastDue} days. Serving one now would put a notice on the record that the "
                        + "account had not earned.");
                }

                var notice = DunningNotice.Serve(
                    step,
                    account.Id,
                    account.AccountNumber,
                    customer.Id,
                    customer.Name,
                    servedOn,
                    arrears.PastDueAmount,
                    arrears.Currency,
                    arrears.DaysPastDue,
                    input.Notes,
                    RegistryActor.Of(currentUser),
                    now);

                database.DunningNotices.Add(notice);

                // INVARIANT 1, and more than that: this entry and the row it describes are together
                // the evidence that the customer was warned before their supply was cut off.
                audit.Record(
                    AuditActions.DunningNoticeServed,
                    AuditEntityTypes.DunningNotice,
                    notice.Id.ToString(),
                    before: null,
                    after: DunningNoticeSnapshot.Of(notice));

                // No event. Nothing downstream consumes a served notice — WP-2.21 reads the register
                // through this service — and an event nobody consumes is an instruction rather than
                // a fact (WP-2.15's rule, WP-2.16's again).
                return notice;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<DisconnectionEvaluation> EvaluateAsync(
        Guid serviceAccountId,
        EvaluateDisconnectionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                // DEMANDED HERE, not only inside the deposit ledger. An evaluation moves a customer's
                // deposit, so it is a deposit act — and an account holding nothing would otherwise
                // slip past the gate simply because there was no movement to refuse. WORK_PACKAGES.md
                // asks for "an offset attempted without permission → 403", and this is what makes
                // that answer independent of what the customer happens to hold.
                RequireDepositPermission();

                var now = clock.GetUtcNow();
                var asOf = input.AsOf ?? DateOnly.FromDateTime(now.UtcDateTime);

                var (account, customer) = await LoadAsync(serviceAccountId, ct).ConfigureAwait(false);

                var arrears = await bills.ArrearsForAccountAsync(account.Id, asOf, ct).ConfigureAwait(false);
                var steps = await StepsAsync(ct).ConfigureAwait(false);
                var notices = await ListNoticesAsync(account.Id, MaxNotices, ct).ConfigureAwait(false);
                var standing = await arrangements.StandingForAccountAsync(account.Id, ct).ConfigureAwait(false);

                var plan = DisconnectionRules.Decide(
                    arrears,
                    customer.DepositHeld,
                    RequireDisconnectionStep(steps),
                    LatestDisconnectionNotice(notices),
                    standing,
                    isOffsetApplied: false);

                var entries = await OffsetAsync(customer.Id, arrears, plan.OffsetAmount, ct).ConfigureAwait(false);

                // The plan's own figures, marked as done. They are what actually happened: the offset
                // is the lesser of what was held and what was past due, applied oldest bill first
                // until it runs out, and the loop below can neither exceed nor fall short of it.
                var eligibility = plan with { IsOffsetApplied = true };

                var evaluation = new DisconnectionEvaluation(eligibility, entries);

                // ONE entry for the evaluation, carrying every figure the answer turned on. Each
                // deposit movement it made already has its own CustomerDepositApplied entry naming
                // the bill and the statute; this is the entry that says why they were made at all.
                audit.Record(
                    AuditActions.DisconnectionEligibilityEvaluated,
                    AuditEntityTypes.ServiceAccount,
                    account.Id.ToString(),
                    before: null,
                    after: DisconnectionEvaluationSnapshot.Of(account.AccountNumber, evaluation));

                return evaluation;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DunningNotice>> ListNoticesAsync(
        Guid serviceAccountId,
        int limit,
        CancellationToken cancellationToken = default) =>
        await database.DunningNotices
            .AsNoTracking()
            .Where(notice => notice.ServiceAccountId == serviceAccountId)

            // Ordered by key: ids are Guid v7, so the primary-key index already orders
            // chronologically on Postgres and on the fast tier's SQLite alike.
            .OrderByDescending(notice => notice.Id)
            .Take(Math.Clamp(limit, 1, MaxNotices))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Applies <paramref name="offset"/> of the held deposit to the past-due bills, oldest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Oldest first, because that is what "qualifying past-due amounts" means to a debtor.</b> A
    /// deposit that settled the newest bill and left a year-old one standing would leave the account
    /// exactly as delinquent as it was and cost the customer their deposit.
    /// </para>
    /// <para>
    /// <b>One movement per bill.</b> A <c>DepositEntry</c> names the bill it settled, and Billing
    /// reduces exactly that bill when it consumes the event — so an offset spanning three bills is
    /// three entries and three reductions, not one entry with a note.
    /// </para>
    /// </remarks>
    private async Task<List<DepositEntry>> OffsetAsync(
        Guid customerId,
        AccountArrears arrears,
        decimal offset,
        CancellationToken cancellationToken)
    {
        var entries = new List<DepositEntry>();
        var remaining = offset;

        foreach (var bill in arrears.Bills.Where(bill => bill.IsPastDue))
        {
            if (remaining <= Money.Zero)
            {
                break;
            }

            var amount = Math.Min(remaining, bill.Balance);

            if (amount <= Money.Zero)
            {
                continue;
            }

            var entry = await deposits.ApplyAsync(
                    customerId,
                    new ApplyDepositInput(bill.Id, amount, StatutoryBasis.OffsetReason(bill.BillNumber)),
                    cancellationToken)
                .ConfigureAwait(false);

            entries.Add(entry);
            remaining -= amount;
        }

        return entries;
    }

    /// <summary>The published sequence, read from the table rather than from <see cref="DunningSequence.All"/>.</summary>
    /// <remarks>
    /// The same call <c>DepositRuleService</c> and <c>FeeScheduleService</c> make: the static list is
    /// how the rows are <i>seeded</i>, and what is in force is whatever the database holds — so
    /// changing a threshold is a migration and not a redeploy of the domain.
    /// </remarks>
    private async Task<IReadOnlyList<DunningStep>> StepsAsync(CancellationToken cancellationToken) =>
        await database.DunningSteps
            .AsNoTracking()
            .OrderBy(step => step.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The disconnection step, which is the one eligibility is judged against.</summary>
    /// <exception cref="RegistryValidationException">The sequence publishes none.</exception>
    private static DunningStep RequireDisconnectionStep(IEnumerable<DunningStep> steps) =>
        steps.FirstOrDefault(step => step.NoticeType is DunningNoticeType.Disconnection)
        ?? throw new RegistryValidationException(
            "No disconnection step is published, so no account can be judged for disconnection. The dunning "
            + "sequence is reference data: add the row in a migration.");

    /// <summary>
    /// The most recent disconnection notice served, or <see langword="null"/> where none has been.
    /// </summary>
    /// <remarks>
    /// <b>The most recent, not the first.</b> An account that cleared its arrears, fell behind again
    /// and was served again is entitled to the second notice's waiting period — judging on the first
    /// would cut somebody off on a clock that ran out while they were up to date.
    /// </remarks>
    private static DunningNotice? LatestDisconnectionNotice(IEnumerable<DunningNotice> notices) =>
        notices
            .Where(notice => notice.NoticeType is DunningNoticeType.Disconnection)
            .OrderByDescending(notice => notice.ServedOn)
            .ThenByDescending(notice => notice.Id)
            .FirstOrDefault();

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

        // Untracked: nothing here writes to the customer. The deposit offset does, and it loads its
        // own tracked copy through CustomerDepositService — which is the only thing that may move
        // DepositHeld.
        var customer = await database.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == account.CustomerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CustomerNotFoundException(account.CustomerId);

        return (account, customer);
    }

    /// <exception cref="RegistryPermissionException">The caller does not hold <c>customers.deposit</c>.</exception>
    private void RequireDepositPermission()
    {
        if (currentUser.HasPermission(Permissions.Customers.Deposit))
        {
            return;
        }

        throw new RegistryPermissionException(
            $"Evaluating an account for disconnection sets its security deposit against what is past due, so it "
            + $"requires the '{Permissions.Customers.Deposit}' permission. The delinquency picture is a read and "
            + "needs no such grant.");
    }
}

/// <summary>The shape a served notice is audited as.</summary>
/// <param name="Id">Which notice.</param>
/// <param name="ServiceAccountId">The account served over.</param>
/// <param name="AccountNumber">Its number, so the entry reads without a second lookup.</param>
/// <param name="NoticeType">Which notice went out.</param>
/// <param name="ServedOn">The day it went out — what the statutory clock runs from.</param>
/// <param name="ArrearsAmount">What was past due then.</param>
/// <param name="Currency">ISO 4217 code that figure is in.</param>
/// <param name="DaysPastDue">How late the oldest past-due bill was.</param>
/// <param name="WaitingPeriodDays">The period it started, in days.</param>
/// <param name="EffectiveFrom">The first day the act it warns of may be taken.</param>
public sealed record DunningNoticeSnapshot(
    Guid Id,
    Guid ServiceAccountId,
    string AccountNumber,
    DunningNoticeType NoticeType,
    DateOnly ServedOn,
    decimal ArrearsAmount,
    string Currency,
    int DaysPastDue,
    int WaitingPeriodDays,
    DateOnly? EffectiveFrom)
{
    /// <summary>Takes a snapshot of <paramref name="notice"/>.</summary>
    public static DunningNoticeSnapshot Of(DunningNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        return new DunningNoticeSnapshot(
            notice.Id,
            notice.ServiceAccountId,
            notice.AccountNumber,
            notice.NoticeType,
            notice.ServedOn,
            notice.ArrearsAmount,
            notice.Currency,
            notice.DaysPastDue,
            notice.WaitingPeriodDays,
            notice.EffectiveFrom);
    }
}

/// <summary>
/// The shape a disconnection evaluation is audited as: every figure the answer turned on, and the
/// deposit entries it wrote.
/// </summary>
/// <param name="ServiceAccountId">The account judged.</param>
/// <param name="AccountNumber">Its number.</param>
/// <param name="AsOf">The day judged against.</param>
/// <param name="Currency">ISO 4217 code every figure is in.</param>
/// <param name="ArrearsBeforeOffset">What was past due before the deposit was set against it.</param>
/// <param name="DepositHeldBeforeOffset">What was held.</param>
/// <param name="OffsetAmount">What was applied under the statute.</param>
/// <param name="ArrearsAfterOffset">What remained past due.</param>
/// <param name="Threshold">The published arrears the disconnection step asks for.</param>
/// <param name="StatutoryBasis">The law the offset was made under.</param>
/// <param name="DepositEntryIds">The ledger entries it wrote.</param>
/// <param name="IsEligible">The answer.</param>
/// <param name="Blockers">What stood in the way, where it was not.</param>
public sealed record DisconnectionEvaluationSnapshot(
    Guid ServiceAccountId,
    string AccountNumber,
    DateOnly AsOf,
    string Currency,
    decimal ArrearsBeforeOffset,
    decimal DepositHeldBeforeOffset,
    decimal OffsetAmount,
    decimal ArrearsAfterOffset,
    decimal Threshold,
    string StatutoryBasis,
    IReadOnlyList<Guid> DepositEntryIds,
    bool IsEligible,
    IReadOnlyList<string> Blockers)
{
    /// <summary>Takes a snapshot of <paramref name="evaluation"/>.</summary>
    public static DisconnectionEvaluationSnapshot Of(string accountNumber, DisconnectionEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        var eligibility = evaluation.Eligibility;

        return new DisconnectionEvaluationSnapshot(
            eligibility.ServiceAccountId,
            accountNumber,
            eligibility.AsOf,
            eligibility.Currency,
            eligibility.ArrearsBeforeOffset,
            eligibility.DepositHeldBeforeOffset,
            eligibility.OffsetAmount,
            eligibility.ArrearsAfterOffset,
            eligibility.Threshold,
            Delinquency.StatutoryBasis.PublicLaw1617,
            [.. evaluation.OffsetEntries.Select(entry => entry.Id)],
            eligibility.IsEligible,
            eligibility.Blockers);
    }
}
