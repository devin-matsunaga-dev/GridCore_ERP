using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Fees;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.Features.Delinquency;

/// <summary>What a caller supplies to run the late-charge job.</summary>
/// <param name="AsOf">
/// The day to judge against. Today when the caller does not say. Its own field so a run can be
/// re-done for a month that was missed, and so a test is not at the mercy of the calendar.
/// </param>
/// <param name="ServiceAccountId">
/// One account only, where a rep is putting one right. The whole register when null — which is what
/// the monthly job does.
/// </param>
public sealed record LateChargeRunInput(DateOnly? AsOf = null, Guid? ServiceAccountId = null);

/// <summary>One bill the run considered and did not charge, and why.</summary>
/// <param name="BillId">The bill.</param>
/// <param name="BillNumber">Its number, as printed.</param>
/// <param name="Reason">Why it was passed over, in words a rep reading the run can act on.</param>
public sealed record LateChargeSkip(Guid BillId, string BillNumber, string Reason);

/// <summary>What one late-charge run did.</summary>
/// <param name="AsOf">The day it judged against.</param>
/// <param name="PeriodStart">The first day of the month it charged for.</param>
/// <param name="Assessed">Every assessment it wrote, and so every charge it raised.</param>
/// <param name="Skipped">Every past-due bill it considered and passed over, with the reason.</param>
public sealed record LateChargeRunResult(
    DateOnly AsOf,
    DateOnly PeriodStart,
    IReadOnlyList<LateChargeAssessment> Assessed,
    IReadOnlyList<LateChargeSkip> Skipped)
{
    /// <summary>How many bills were charged.</summary>
    public int ChargedCount => Assessed.Count;

    /// <summary>What was charged in total.</summary>
    public decimal TotalCharged => Money.Total(Assessed.Select(assessment => assessment.Amount));
}

/// <summary>The late-charge run: one per cent a month of what is past due (WP-2.19).</summary>
public interface ILateChargeService
{
    /// <summary>
    /// Charges every past-due bill that has not already been charged for the month
    /// <paramref name="input"/> falls in.
    /// </summary>
    /// <exception cref="BillingPermissionException">The caller may not charge fees.</exception>
    /// <exception cref="BillingValidationException">The schedule publishes no late-charge rate on that day.</exception>
    Task<LateChargeRunResult> RunAsync(LateChargeRunInput input, CancellationToken cancellationToken = default);

    /// <summary>What has been assessed against one account, newest period first.</summary>
    Task<IReadOnlyList<LateChargeAssessment>> ListAsync(
        Guid serviceAccountId,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The late-charge run over the billing schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>It raises charges through <see cref="IAccountChargeService"/> and never writes one itself.</b>
/// A late charge is an ordinary WP-2.16 account charge — it lands on the next cycle bill, it can be
/// withdrawn, it stamps the schedule row that priced it — and a second code path that produced
/// charges would be a second set of rules about what a charge is. What this service adds is the
/// <i>deciding</i>: which bills, on what basis, and not twice.
/// </para>
/// <para>
/// <b>The whole run is one unit of work.</b> Every charge, every assessment row and the run's audit
/// entry commit together, so a run that fails half way leaves the register exactly as it found it —
/// which matters more here than almost anywhere else, because the alternative is a customer holding
/// a bill for a charge whose assessment row was never written and which the next run would raise
/// again.
/// </para>
/// <para>
/// <b>Per bill, not per account.</b> WORK_PACKAGES.md asks for idempotency "per bill per period",
/// and that is also the honest arithmetic: a customer three bills behind is late three times over,
/// each bill for its own balance, and one charge on the account total would be a figure no line of
/// the ageing accounts for.
/// </para>
/// </remarks>
public sealed class LateChargeService(
    BillingDbContext database,
    IAccountChargeService charges,
    IFeeScheduleService schedule,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider clock) : ILateChargeService
{
    /// <summary>Most bills one run will consider in a pass.</summary>
    /// <remarks>
    /// Generous rather than tight, the call <c>BillService.MaxReviewSize</c> makes about the overdue
    /// review beside it: this exists so a data fault cannot turn one job into an unbounded read, not
    /// because a utility is expected to have five hundred past-due bills in a month.
    /// </remarks>
    public const int MaxRunSize = 500;

    /// <summary>The largest page <see cref="ListAsync"/> will answer, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public Task<LateChargeRunResult> RunAsync(LateChargeRunInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                // Demanded here as well as on the route, and demanded again by RaiseAsync for every
                // charge. This one is what refuses the run before it has read a single bill.
                RequireChargePermission();

                var now = clock.GetUtcNow();
                var asOf = input.AsOf ?? DateOnly.FromDateTime(now.UtcDateTime);
                var period = LateChargeAssessment.PeriodOf(asOf);
                var actor = RegistryActor.Of(currentUser);

                // The rate, read ONCE for the whole run and on the day being judged. Reading it per
                // bill would be the same query five hundred times, and reading it as "today" would
                // price a re-run of last month's job at this month's rate.
                var quote = await schedule.AssessAsync(FeeCode.LateCharge, asOf, ct).ConfigureAwait(false);

                var candidates = await PastDueAsync(asOf, input.ServiceAccountId, ct).ConfigureAwait(false);

                // One query for everything already charged this period, rather than one per bill.
                var billIds = candidates.ConvertAll(bill => bill.Id);

                var alreadyCharged = await database.LateChargeAssessments
                    .AsNoTracking()
                    .Where(assessment => assessment.PeriodStart == period && billIds.Contains(assessment.BillId))
                    .Select(assessment => assessment.BillId)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                var charged = alreadyCharged.ToHashSet();

                var assessed = new List<LateChargeAssessment>();
                var skipped = new List<LateChargeSkip>();

                foreach (var bill in candidates)
                {
                    if (charged.Contains(bill.Id))
                    {
                        // THE IDEMPOTENCY, as the run sees it. The unique index is what guarantees
                        // it; this is what lets a second run finish quietly instead of throwing.
                        skipped.Add(new LateChargeSkip(
                            bill.Id,
                            bill.BillNumber,
                            $"Already charged for {period:MMMM yyyy}."));

                        continue;
                    }

                    var priced = quote.PriceOn(bill.Balance);

                    // A charge that rounds away to nothing is not raised. AccountCharge.Raise would
                    // refuse it anyway — a line reading "0.00" on a customer's bill is the thing
                    // WP-2.16 refuses — and NO assessment row is written, so a balance that grows
                    // past the rounding floor is charged next month rather than being written off
                    // by a run that recorded having considered it.
                    if (priced.Amount is not { } amount || amount < Money.Round(0.01m))
                    {
                        skipped.Add(new LateChargeSkip(
                            bill.Id,
                            bill.BillNumber,
                            $"{quote.Rate:P2} of {bill.Balance:0.00} rounds to nothing."));

                        continue;
                    }

                    var daysPastDue = ArrearsAgeing.DaysPastDue(bill.DueDate, asOf);

                    var charge = await charges.RaiseAsync(
                            new RaiseChargeInput(
                                bill.ServiceAccountId,
                                FeeCode.LateCharge,
                                $"Late payment charge for {period:MMMM yyyy}: {quote.Rate:P2} of {bill.Balance:0.00} "
                                + $"past due on bill {bill.BillNumber}, {daysPastDue} days overdue.",
                                asOf,
                                bill.Balance),
                            ct)
                        .ConfigureAwait(false);

                    var assessment = LateChargeAssessment.For(
                        charge,
                        bill.Id,
                        bill.BillNumber,
                        period,
                        asOf,
                        daysPastDue,
                        actor,
                        now);

                    database.LateChargeAssessments.Add(assessment);
                    assessed.Add(assessment);
                }

                var result = new LateChargeRunResult(asOf, period, assessed, skipped);

                // ONE entry for the run, for the reason a billing run and an overdue review each get
                // one: it is one act, and an entry per bill would bury "who ran the late charges and
                // what did they come to" under fifty rows saying the same thing. Every charge it
                // raised already has its own AccountChargeRaised entry.
                audit.Record(
                    AuditActions.LateChargeRun,
                    AuditEntityTypes.LateChargeRun,
                    period.ToString("yyyy-MM"),
                    before: null,
                    after: LateChargeRunSnapshot.Of(result, quote));

                return result;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LateChargeAssessment>> ListAsync(
        Guid serviceAccountId,
        int limit,
        CancellationToken cancellationToken = default) =>
        await database.LateChargeAssessments
            .AsNoTracking()
            .Where(assessment => assessment.ServiceAccountId == serviceAccountId)

            // Ordered by key: ids are Guid v7, so the primary-key index already orders
            // chronologically on Postgres and on the fast tier's SQLite alike.
            .OrderByDescending(assessment => assessment.Id)
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// The past-due bills a run considers: still owed, and past their due date on
    /// <paramref name="asOf"/>.
    /// </summary>
    /// <remarks>
    /// <b>The same predicate the ageing uses and the overdue review moves on</b> — <c>due &lt;
    /// asOf</c>, so a customer has the whole of the due day. It is spelled out rather than calling
    /// <c>ArrearsAgeing.IsPastDue</c> because EF has to turn it into SQL, and a method call over a
    /// nullable date is not something it can.
    /// </remarks>
    private async Task<List<Bill>> PastDueAsync(DateOnly asOf, Guid? serviceAccountId, CancellationToken cancellationToken)
    {
        var bills = database.Bills
            .AsNoTracking()
            .Where(bill =>
                bill.Status == BillStatus.Issued
                || bill.Status == BillStatus.PartiallyPaid
                || bill.Status == BillStatus.Overdue)
            .Where(bill => bill.DueDate != null && bill.DueDate < asOf);

        if (serviceAccountId is { } account)
        {
            bills = bills.Where(bill => bill.ServiceAccountId == account);
        }

        return await bills
            .OrderBy(bill => bill.Id)
            .Take(MaxRunSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a caller who may not charge fees.
    /// </summary>
    /// <exception cref="BillingPermissionException">The caller does not hold <c>billing.charge</c>.</exception>
    private void RequireChargePermission()
    {
        if (!currentUser.HasPermission(Permissions.Billing.Charge))
        {
            throw new BillingPermissionException(
                $"Running the late charges needs '{Permissions.Billing.Charge}'. It raises a published fee "
                + "against every past-due bill, one customer at a time.");
        }
    }
}

/// <summary>The shape a late-charge run is audited as.</summary>
/// <param name="AsOf">The day it judged against.</param>
/// <param name="PeriodStart">The month it charged for.</param>
/// <param name="Rate">The published rate it took, so the entry explains its own figures.</param>
/// <param name="FeeScheduleId">The schedule row that published it.</param>
/// <param name="ChargedCount">How many bills were charged.</param>
/// <param name="TotalCharged">What that came to.</param>
/// <param name="SkippedCount">How many past-due bills were passed over.</param>
/// <param name="Currency">ISO 4217 code the figures are expressed in.</param>
public sealed record LateChargeRunSnapshot(
    DateOnly AsOf,
    DateOnly PeriodStart,
    decimal? Rate,
    Guid FeeScheduleId,
    int ChargedCount,
    decimal TotalCharged,
    int SkippedCount,
    string Currency)
{
    /// <summary>Takes a snapshot of <paramref name="result"/>, priced by <paramref name="quote"/>.</summary>
    public static LateChargeRunSnapshot Of(LateChargeRunResult result, FeeAssessment quote)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(quote);

        return new LateChargeRunSnapshot(
            result.AsOf,
            result.PeriodStart,
            quote.Rate,
            quote.FeeScheduleId,
            result.ChargedCount,
            result.TotalCharged,
            result.Skipped.Count,
            quote.Currency);
    }
}
