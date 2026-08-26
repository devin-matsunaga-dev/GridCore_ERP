using System.Globalization;
using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Fees;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Rating;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.Seeding;

/// <summary>
/// A year of bills across the demo utility's accounts, so the register opens with money owed, a
/// tariff change to see the effect of, and every bill status on screen.
/// </summary>
/// <remarks>
/// <para>
/// Every bill here goes through the real <see cref="RateEngine"/>, the real
/// <see cref="RatePlanSelector"/> and the real <see cref="Bill"/> aggregate. Nothing is assigned:
/// the lines, the totals and the transitions are produced by the same code a live billing run uses,
/// so an impossible demo figure fails at startup naming the bill rather than shipping a number
/// nothing explains. The same call <see cref="MetersDemoSeeder"/> and
/// <c>MeterReadingsDemoSeeder</c> make.
/// </para>
/// <para>
/// The readings and the accounts come from the two <c>Contracts</c> directories, never from a query
/// — this module may read neither the metering nor the customers schema, and a seeder is not an
/// exception to a boundary rule.
/// </para>
/// <para>
/// <b>Two bills carry a correction</b> (WP-2.4) — one credited, one charged — so the demo opens
/// with the sensitive action already visible in the register and in the audit trail, and with a
/// bill whose printed total and amount owed deliberately disagree. Both go through the real
/// <see cref="Bill.Adjust"/>, so a correction the aggregate would refuse fails at startup instead
/// of shipping.
/// </para>
/// <para>
/// <b>Nothing is paid.</b> <c>PartiallyPaid</c> and <c>Paid</c> are reachable —
/// <see cref="Bill.RecordPayment"/> exists and is unit-tested — but a payment invented here would
/// be money with no record in the Payments module of where it came from. WP-2.5 owns paying these
/// bills, and until then the demo world's Draft, Issued, Overdue and Cancelled bills are the honest
/// set.
/// </para>
/// </remarks>
public sealed class BillsDemoSeeder(
    BillingDbContext database,
    IMeterReadingDirectory readings,
    IServiceAccountDirectory accounts,
    TimeProvider clock) : IDemoSeeder
{
    /// <summary>How many monthly cycles are billed, ending with last month's.</summary>
    public const int Cycles = 12;

    /// <summary>
    /// The account number put on the commercial tariff, so the demo world shows a tariff somebody
    /// chose beside the accounts that fall back to the default — and so both tariff shapes,
    /// inclining and declining block, appear on real bills.
    /// </summary>
    public const string CommercialAccountNumber = "A-000004";

    /// <summary>
    /// How many of the most recent cycles are left as drafts, unissued. A billing officer opens the
    /// demo with work to do rather than with a finished month.
    /// </summary>
    private const int DraftCycles = 1;

    /// <summary>
    /// Most readings one seeded cycle will bill. The reading seeder lays twelve cycles across a
    /// handful of meters, so this is a ceiling rather than a limit anything reaches.
    /// </summary>
    private const int MaxCycleSize = 500;

    /// <summary>Days after the meter is read that a seeded bill goes out.</summary>
    private const int IssueLagDays = 3;

    /// <summary>Which cycle's bill is cancelled, counted from the oldest.</summary>
    private const int CancelledCycleIndex = 1;

    /// <summary>
    /// How many bills the seeded world raises beyond one per cycle: the single counter bill that
    /// carries a fee paid at the desk (WP-2.16). Named so the count a test asserts says why it is
    /// what it is.
    /// </summary>
    public const int CounterBills = 1;

    /// <summary>
    /// How much of a disputed bill is credited or charged, as a fraction of what is owed. A
    /// proportion rather than a figure, so the correction cannot be larger than the bill it is made
    /// against however the seeded consumption moves — which <see cref="Bill.Adjust"/> would refuse.
    /// </summary>
    private const decimal CorrectionShare = 0.25m;

    /// <summary>Who the seeded bills are attributed to — a stand-in colleague, holding no permissions.</summary>
    public static DemoActor Officer { get; } = new("billing", "Marisa Camacho (demo)");

    private static RegistryActor Attribution { get; } = RegistryActor.Of(Officer);

    /// <inheritdoc />
    /// <remarks>The dedupe key. Never renamed — a rename seeds a second year of bills.</remarks>
    public string Name => "billing.bills";

    /// <inheritdoc />
    /// <remarks>After the reading register (700), whose cycles this one bills.</remarks>
    public int Order => 800;

    /// <inheritdoc />
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var plans = await database.RatePlans
            .AsNoTracking()
            .Include(plan => plan.Tiers)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (plans.Count is 0)
        {
            // No tariffs, nothing to bill on. Unreachable on a migrated database — the plans ship by
            // migration — and a silent return rather than a throw because a seeder must not be the
            // thing that decides reference data is missing.
            return;
        }

        var cycles = CycleCodes(today).ToList();
        var billedAccounts = new HashSet<Guid>();
        var raised = new List<Bill>();
        var tariffs = new Dictionary<Guid, string>();
        var step = 0;
        var ordinal = 0;

        // Ids are Guid v7 stamped from the instant they are created, and rows created in the same
        // instant have no defined order. A step per write keeps the register in the order the cycles
        // were actually billed.
        DateTimeOffset Next() => now.AddMilliseconds(step++);

        for (var index = 0; index < cycles.Count; index++)
        {
            var cycleCode = cycles[index];
            var cycle = await readings.ForCycleAsync(cycleCode, MaxCycleSize, cancellationToken).ConfigureAwait(false);

            if (cycle.Count is 0)
            {
                continue;
            }

            var openAccounts = await accounts
                .FindOpenAtLocationsAsync([.. cycle.Select(reading => reading.ServiceLocationId)], cancellationToken)
                .ConfigureAwait(false);

            // The one account somebody has deliberately put on the commercial tariff. Assigned on
            // the first cycle that reaches it, through the real aggregate, so the assignment carries
            // a real actor and a real timestamp.
            AssignCommercialTariff(openAccounts.Values, tariffs, Next());

            var billedThisCycle = new HashSet<Guid>();

            foreach (var reading in cycle)
            {
                if (reading.IsException
                    || reading.Consumption is not { } consumption
                    || reading.PreviousReadingDate is not { } from
                    || !openAccounts.TryGetValue(reading.ServiceLocationId, out var account)
                    || account.ServiceStartedAt is null
                    || !billedThisCycle.Add(account.Id))
                {
                    // Exactly the rules BillService.Reject applies, in the same order. Duplicated
                    // rather than shared because a seeder that called the service would need a unit
                    // of work of its own — and a demo world silently missing a bill is easier to
                    // spot than one that fails at startup for a premise nobody has an account at.
                    continue;
                }

                var periodEnd = DateOnly.FromDateTime(reading.ReadingDate.UtcDateTime);
                var code = tariffs.GetValueOrDefault(account.Id, DefaultRatePlans.DefaultCode);

                // Effective dating for real: the cycles straddle the residential repricing, so the
                // bills before July 2026 carry the old rates and the ones after carry the new.
                if (RatePlanSelector.InForceOn(plans, code, periodEnd) is not { } plan)
                {
                    continue;
                }

                var bill = Bill.Calculate(
                    RegistryNumbers.Format(BillNumbers.BillNumberPrefix, ++ordinal),
                    account,
                    new BilledReading(reading.Id, reading.MeterId, reading.MeterNumber, reading.PreviousReading, reading.Reading),
                    RateEngine.Calculate(plan, [.. plan.Tiers], consumption),
                    DateOnly.FromDateTime(from.UtcDateTime),
                    periodEnd,
                    Attribution,
                    Next(),
                    cycleCode);

                Progress(bill, index, cycles.Count, periodEnd, today, billedAccounts);

                database.Bills.Add(bill);
                raised.Add(bill);
            }
        }

        Correct(raised, Next);

        await RaiseFeesAsync(
            raised,
            () => RegistryNumbers.Format(BillNumbers.BillNumberPrefix, ++ordinal),
            Next,
            today,
            cancellationToken).ConfigureAwait(false);

        // No SaveChanges: the runner's unit of work saves these and the seed record in one
        // transaction, which is what makes a half-billed demo cycle impossible.
    }

    /// <summary>
    /// Raises two fees off the published schedule (WP-2.16): one left waiting for the next bill, one
    /// already paid for at the counter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two states, because one would not show what the register is for.</b> The pending charge is
    /// what a reviewer sees land when they run the next billing cycle from the demonstration screen —
    /// which is the whole of "it lands on the next bill", visible rather than described. The billed
    /// one is a charge bill: a document with a fee line, no meter and no tariff, which is the shape
    /// nothing else in the demo world has.
    /// </para>
    /// <para>
    /// Through the real <see cref="AccountCharge"/> aggregate and the real
    /// <see cref="FeeScheduleSelector"/>, as every other seeded row goes through the real code that
    /// makes it — so a fee the schedule does not publish fails at startup rather than shipping. The
    /// service is deliberately NOT used: it demands <c>billing.charge</c>, and the demo officer holds
    /// no permissions at all (see <see cref="Officer"/>), which is the same reason the bills above go
    /// through the aggregate rather than through <c>IBillService</c>.
    /// </para>
    /// </remarks>
    private async Task RaiseFeesAsync(
        IEnumerable<Bill> raised,
        Func<string> nextBillNumber,
        Func<DateTimeOffset> next,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var schedule = await database.FeeSchedule.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        // The two most recently billed accounts, chosen the way Correct chooses its two and for the
        // same reason: bill numbers are issued in order, so this is deterministic where ordering by
        // id is not.
        var billed = raised
            .OrderByDescending(bill => bill.BillNumber, StringComparer.Ordinal)
            .DistinctBy(bill => bill.ServiceAccountId)
            .Take(2)
            .ToList();

        if (billed.Count < 2)
        {
            // A demo world too small to have two billed accounts. Nothing to charge, and a seeder
            // must not be the thing that decides that is a failure.
            return;
        }

        // Through the directory, never fabricated off the bill: a charge stamps the account as it
        // stands now, and this module has no business inventing a service account's own facts.
        var summaries = await accounts
            .FindManyAsync([.. billed.Select(bill => bill.ServiceAccountId)], cancellationToken)
            .ConfigureAwait(false);

        if (!summaries.TryGetValue(billed[0].ServiceAccountId, out var first)
            || !summaries.TryGetValue(billed[1].ServiceAccountId, out var second))
        {
            return;
        }

        var waiting = Raise(
            FeeCode.Reconnection,
            first,
            "Supply restored after the account was settled; fee raised for the next bill.",
            today,
            schedule,
            next());

        var atTheCounter = Raise(
            FeeCode.MeterTest,
            second,
            "Customer asked for their meter to be tested and paid the fee at the desk.",
            today,
            schedule,
            next());

        if (waiting is null || atTheCounter is null)
        {
            return;
        }

        database.AccountCharges.Add(waiting);
        database.AccountCharges.Add(atTheCounter);

        var at = next();

        var counterBill = Bill.ForCharges(
            nextBillNumber(),
            second,
            [atTheCounter.AsBillLine()],
            atTheCounter.Currency,
            today,
            Attribution,
            at);

        // Issued, not left a draft: a charge bill exists because somebody was standing at the
        // counter, and a draft one would be a document nobody was ever handed.
        counterBill.Issue(today, today.AddDays(BillingTerms.DueDays), Attribution, at, "Fee paid at the counter.");

        atTheCounter.MarkBilled(counterBill.Id, counterBill.BillNumber, at);

        database.Bills.Add(counterBill);
    }

    /// <summary>
    /// Raises one fee against an account, or <see langword="null"/> where the schedule publishes no
    /// figure for it today.
    /// </summary>
    private static AccountCharge? Raise(
        FeeCode code,
        ServiceAccountSummary account,
        string reason,
        DateOnly today,
        IReadOnlyList<FeeScheduleEntry> schedule,
        DateTimeOffset at) =>
        FeeScheduleSelector.InForceOn(schedule, code, today) is { } entry
            ? AccountCharge.Raise(FeeAssessment.Of(entry), account, today, reason, Attribution, at)
            : null;

    /// <summary>
    /// Corrects two of the seeded bills — one credited, one charged — through the real aggregate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two most recently billed accounts that still owe money, so the corrections sit near the
    /// top of the register where a reviewer will see them rather than a year down the list. Bill
    /// numbers are issued in order, so ordering by them is deterministic — unlike ordering by id,
    /// where rows minted inside one millisecond have no defined order at all.
    /// </para>
    /// <para>
    /// Two different bills rather than two corrections to one: a bill with a credit and a charge
    /// cancelling out would show an adjustment history and an unchanged amount owed, which is the
    /// one arrangement that teaches a reviewer nothing.
    /// </para>
    /// </remarks>
    private static void Correct(IEnumerable<Bill> raised, Func<DateTimeOffset> next)
    {
        var candidates = raised
            .Where(bill => bill.IsOutstanding)
            .OrderByDescending(bill => bill.BillNumber, StringComparer.Ordinal)
            .DistinctBy(bill => bill.ServiceAccountId)
            .Take(2)
            .ToList();

        if (candidates.Count < 2)
        {
            // A demo world too small to have two outstanding bills on two accounts. Nothing to
            // correct, and a seeder must not be the thing that decides that is a failure.
            return;
        }

        Adjust(
            candidates[0],
            BillAdjustmentKind.Credit,
            "Estimated read corrected after the customer disputed it; re-based on the following actual read.",
            next());

        Adjust(
            candidates[1],
            BillAdjustmentKind.Charge,
            "Under-billed: the first tier was applied to units that fall in the second.",
            next());
    }

    /// <summary>Applies one correction, or leaves the bill alone if the share rounds to nothing.</summary>
    private static void Adjust(Bill bill, BillAdjustmentKind kind, string reason, DateTimeOffset at)
    {
        var amount = Money.Round(bill.Balance * CorrectionShare);

        if (amount <= Money.Zero)
        {
            return;
        }

        bill.Adjust(kind, amount, reason, Attribution, at);
    }

    /// <summary>
    /// Walks a seeded bill to where it should stand: recent cycles stay drafts, older ones are
    /// issued, the ones whose due date has passed go overdue, and the very first account to be
    /// billed twice has its second bill cancelled.
    /// </summary>
    /// <remarks>
    /// Every one of these is a real transition through the real aggregate, so the demo world's
    /// statuses are ones the state machine actually allows — the same call
    /// <c>ServiceAccountsDemoSeeder</c> makes by walking accounts through <c>Start</c> and
    /// <c>Stop</c> rather than assigning a status.
    /// </remarks>
    private static void Progress(
        Bill bill,
        int cycleIndex,
        int cycleCount,
        DateOnly periodEnd,
        DateOnly today,
        HashSet<Guid> billedAccounts)
    {
        if (cycleIndex >= cycleCount - DraftCycles)
        {
            // Last month's run, not yet sent. The demo opens with drafts to issue.
            return;
        }

        // Issued a few days after the meter was read, which is when a utility actually bills.
        var issuedOn = periodEnd.AddDays(IssueLagDays);

        bill.Issue(issuedOn, issuedOn.AddDays(BillingTerms.DueDays), Attribution, bill.CreatedAt);

        // The second bill this account ever saw, withdrawn. One cancelled bill in the demo world, on
        // a real reason, so the status is on screen and so the AR figures visibly exclude it.
        if (!billedAccounts.Add(bill.ServiceAccountId) && bill.CycleCode is not null && cycleIndex is CancelledCycleIndex)
        {
            bill.Cancel("Billed against a reading the customer disputed; re-read arranged.", Attribution, bill.CreatedAt);

            return;
        }

        bill.MarkOverdue(today, Attribution, bill.CreatedAt);
    }

    /// <summary>Puts the commercial account on the commercial tariff, once, the first time it appears.</summary>
    private void AssignCommercialTariff(
        IEnumerable<ServiceAccountSummary> openAccounts,
        Dictionary<Guid, string> tariffs,
        DateTimeOffset at)
    {
        var commercial = openAccounts.FirstOrDefault(account =>
            string.Equals(account.AccountNumber, CommercialAccountNumber, StringComparison.Ordinal));

        if (commercial is null || tariffs.ContainsKey(commercial.Id))
        {
            return;
        }

        tariffs[commercial.Id] = DefaultRatePlans.CommercialStandard;

        database.AccountRatePlans.Add(AccountRatePlan.Assign(
            commercial.Id,
            DefaultRatePlans.CommercialStandard,
            Attribution,
            at));
    }

    /// <summary>
    /// The reading cycles to bill: the last <see cref="Cycles"/> complete months, oldest first. The
    /// same codes <c>MeterReadingsDemoSeeder</c> laid down, derived the same way rather than shared
    /// — this module cannot see that class, and the cycle code is the contract between them.
    /// </summary>
    private static IEnumerable<string> CycleCodes(DateOnly today)
    {
        var thisMonth = new DateOnly(today.Year, today.Month, 1);

        for (var cycle = Cycles; cycle >= 1; cycle--)
        {
            yield return thisMonth.AddMonths(-cycle).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        }
    }
}
