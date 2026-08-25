using System.Globalization;
using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Rating;
using GridCore.Modules.Billing.Features.Shared;
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
            }
        }

        // No SaveChanges: the runner's unit of work saves these and the seed record in one
        // transaction, which is what makes a half-billed demo cycle impossible.
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
