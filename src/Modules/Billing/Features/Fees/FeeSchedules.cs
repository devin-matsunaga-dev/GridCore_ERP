using GridCore.Contracts.Services;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Shared;

namespace GridCore.Modules.Billing.Features.Fees;

/// <summary>
/// The fee schedule the utility ships with: reference data, not demo data. A database that has been
/// migrated can charge a fee (ARCHITECTURE.md invariant 8), whether or not anyone ever seeds a demo
/// world.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every figure here is a demo figure and says so in its own description.</b> They follow CUC's
/// published customer-service information, whose own publications disagree with each other on
/// amounts and change without notice — so the application reads a table, the description carries the
/// provenance, and nobody can mistake $135 for an authoritative charge. Changing a figure is a
/// migration, not a redeploy.
/// </para>
/// <para>
/// <b>Two versions of the reconnection fee ship, not one.</b> A fee that only ever had one version
/// makes effective-dating untestable — "pick the version in force" and "pick the only version" are
/// the same answer — and a utility that could not republish a fee would not be one. The same call
/// <see cref="DefaultRatePlans"/> makes about the residential tariff, and it is what lets a charge
/// raised in June and reprinted in August still show June's figure.
/// </para>
/// <para>
/// Adding or repricing a fee is a new migration — the rows are seeded by one, and migrations are
/// append-only (invariant 7). Never change a code's name: it is the row's identity and
/// <c>ReferenceId</c> derives the key from it.
/// </para>
/// </remarks>
public static class FeeSchedules
{
    /// <summary>
    /// The instant this reference set was authored, and the timestamp component of every row id.
    /// Fixed forever: changing it changes every id, which to the database is a different schedule.
    /// A <i>new version</i> of a fee does not need a new instant — the version key already carries
    /// its effective date (<see cref="FeeScheduleEntry.KeyFor"/>).
    /// </summary>
    public static readonly DateTimeOffset AuthoredAt = new(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The day the shipped schedule first applied. The same day the shipped tariffs did, and for the
    /// same reason: a utility's published charges predate the accounts they are raised against, and
    /// a fee schedule that began after the demo world's oldest cycle could price nothing in it.
    /// </summary>
    public static readonly DateOnly OriginalEffectiveFrom = DefaultRatePlans.OriginalEffectiveFrom;

    /// <summary>
    /// The day the reconnection fee was republished at a higher figure. Mid-year on purpose, so the
    /// demo world holds charges on both sides of it.
    /// </summary>
    public static readonly DateOnly ReconnectionRevisionFrom = new(2026, 7, 1);

    /// <summary>
    /// The rate the late charge is published at: one per cent of the past-due balance a month
    /// (WP-2.19).
    /// </summary>
    /// <remarks>
    /// A named constant only because the seed list and its test both have to say the same number;
    /// the <i>authority</i> is the row in <c>billing.fee_schedule</c>, which is what
    /// <c>LateChargeService</c> reads and what a raised charge stamps. Repricing it is a second
    /// effective-dated row in a migration, exactly as repricing the reconnection fee was.
    /// </remarks>
    public const decimal LateChargeMonthlyRate = 0.0100m;

    /// <summary>
    /// The currency the shipped schedule is in. The demo utility bills in US dollars, as the rate
    /// plans and the deposit rules do.
    /// </summary>
    public const string Currency = "USD";

    /// <summary>Every version of every published fee, oldest first.</summary>
    public static IReadOnlyList<FeeScheduleEntry> All { get; } =
    [
        FeeScheduleEntry.Reference(
            FeeCode.ServiceConnection,
            "Service connection fee",
            ServiceType.Electricity,
            135.00m,
            Currency,
            OriginalEffectiveFrom,
            "Levied once when supply is established at a premise, covering the meter and the service drop. "
            + "Demo figure following CUC's published customer-service information; that schedule changes "
            + "without notice, so this is a demo schedule and not an authoritative charge."),

        FeeScheduleEntry.Reference(
            FeeCode.Reconnection,
            "Reconnection fee",
            ServiceType.Electricity,
            50.00m,
            Currency,
            OriginalEffectiveFrom,
            "Levied when supply is restored after it was cut for non-payment. Demo figure following CUC's "
            + "published customer-service information; not an authoritative charge."),

        FeeScheduleEntry.Reference(
            FeeCode.ReturnedPayment,
            "Returned payment fee",
            ServiceType.Electricity,
            25.00m,
            Currency,
            OriginalEffectiveFrom,
            "Levied when a payment that settled is returned unpaid by the bank. Demo figure following CUC's "
            + "published customer-service information; not an authoritative charge."),

        FeeScheduleEntry.Reference(
            FeeCode.MeterTest,
            "Meter test fee",
            ServiceType.Electricity,
            75.00m,
            Currency,
            OriginalEffectiveFrom,
            "Levied when a customer asks for their meter to be tested. Refundable where the meter is found "
            + "to be faulty, which is WP-3.8's business rather than the schedule's. Demo figure following "
            + "CUC's published customer-service information; not an authoritative charge."),

        FeeScheduleEntry.Reference(
            FeeCode.Inspection,
            "Installation inspection fee",
            ServiceType.Electricity,
            50.00m,
            Currency,
            OriginalEffectiveFrom,
            "Levied for inspecting a customer's installation before supply is established. Demo figure "
            + "following CUC's published customer-service information; not an authoritative charge."),

        FeeScheduleEntry.Reference(
            FeeCode.UnauthorizedConnection,
            "Unauthorized connection penalty",
            ServiceType.Electricity,
            550.00m,
            Currency,
            OriginalEffectiveFrom,
            "The penalty for taking supply without an account or interfering with a meter, levied on top of "
            + "an estimate of the unbilled usage. Demo figure following CUC's published customer-service "
            + "information; not an authoritative charge."),

        // THE ONE RATE ROW (WP-2.19). Published as a fraction of what is past due rather than as a
        // figure, because that is how CNMI Public Law 16-17's delinquency regime and CUC's own
        // published terms express it — and because a flat late charge would ask the same of a
        // customer $40 behind as of one $4,000 behind.
        FeeScheduleEntry.ReferenceRate(
            FeeCode.LateCharge,
            "Late payment charge",
            ServiceType.Electricity,
            LateChargeMonthlyRate,
            Currency,
            OriginalEffectiveFrom,
            "One per cent per month of the past-due balance, assessed once per bill per month while it "
            + "remains unpaid. Demo figure following CUC's published customer-service information and the "
            + "delinquency regime of CNMI Public Law 16-17; that schedule changes without notice, so this "
            + "is a demo rate and not an authoritative charge."),

        // The repricing. A second version of one code rather than a seventh code, so effective
        // dating is exercised by the shipped data itself.
        FeeScheduleEntry.Reference(
            FeeCode.Reconnection,
            "Reconnection fee",
            ServiceType.Electricity,
            60.00m,
            Currency,
            ReconnectionRevisionFrom,
            "Republished figure, effective 1 July 2026. Demo figure following CUC's published "
            + "customer-service information; not an authoritative charge."),
    ];

    /// <summary>Every version of <paramref name="code"/>, oldest first.</summary>
    public static IReadOnlyList<FeeScheduleEntry> VersionsOf(FeeCode code) =>
        [.. All.Where(entry => entry.Code == code).OrderBy(entry => entry.EffectiveFrom)];

    /// <summary>
    /// The version of <paramref name="code"/> in force on <paramref name="on"/>, or
    /// <see langword="null"/> if the fee had not been published yet.
    /// </summary>
    public static FeeScheduleEntry? InForceOn(FeeCode code, DateOnly on) => FeeScheduleSelector.InForceOn(All, code, on);

    /// <summary>
    /// Fails if a declared fee has no row, or if two rows claim one code on one day.
    /// </summary>
    /// <remarks>
    /// Called where the schedule is built (<see cref="FeeScheduleConfiguration"/>), so a gap is found
    /// at startup rather than by the clerk who tries to charge it at the counter — the shape
    /// <c>DepositRules.RequireComplete</c> established, and the check WORK_PACKAGES.md asks this
    /// package for by name. <b>Adding a <see cref="FeeCode"/> member without adding its row in the
    /// same migration fails the build's first startup</b>, which is the whole point.
    /// </remarks>
    /// <exception cref="BillingValidationException">A declared fee has no row, or two rows collide.</exception>
    public static void RequireComplete(IEnumerable<FeeScheduleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var rows = entries.ToList();
        var byCode = rows.ToLookup(entry => entry.Code);

        foreach (var code in Enum.GetValues<FeeCode>())
        {
            if (!byCode[code].Any())
            {
                throw new BillingValidationException(
                    $"No fee is published for {code}. A fee schedule is reference data: add the row in a migration, "
                    + "in the same one that declared the code.");
            }
        }

        // Two versions of one fee on one day is a schedule with no answer to "what does this cost
        // today". The unique index refuses the pair in the database; this refuses it in the list the
        // migration is generated from, which is where somebody would actually introduce it.
        var collision = rows
            .GroupBy(entry => entry.VersionKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (collision is not null)
        {
            throw new BillingValidationException(
                $"{collision.Count()} fee schedule rows claim '{collision.Key}'. A fee has one figure on any given day.");
        }
    }
}
