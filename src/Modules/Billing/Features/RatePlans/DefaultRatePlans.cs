using GridCore.Contracts.Services;

namespace GridCore.Modules.Billing.Features.RatePlans;

/// <summary>
/// The tariffs the utility ships with: reference data, not demo data. A database that has been
/// migrated can bill (ARCHITECTURE.md invariant 8), whether or not anyone ever seeds a demo world.
/// </summary>
/// <remarks>
/// <para>
/// Two tariffs, because one is not enough to show that a service account <i>has</i> a tariff rather
/// than the system having one. Residential is inclining-block (heavier use costs more per unit) and
/// commercial is declining-block, which is what real electricity tariffs look like and gives the
/// rate engine two shapes to get right.
/// </para>
/// <para>
/// <b>Three rows, not two (WP-2.3).</b> The residential tariff is published twice: the original and
/// a price revision effective 1 July 2026. A tariff that only ever had one version makes
/// effective-dating untestable — "pick the version in force" and "pick the only version" are the
/// same answer — and a utility that could not reprice would not be one. A bill for June 2026 is
/// billed on the original rates and a bill for August on the July ones, forever, because
/// <see cref="RatePlanSelector"/> chooses by the date of the <i>period</i> and not by today.
/// </para>
/// <para>
/// Adding or changing a plan is a new migration — the rows are seeded by one, and migrations are
/// append-only (invariant 7).
/// </para>
/// </remarks>
public static class DefaultRatePlans
{
    /// <summary>Code of the plan a service account falls back to when it has none of its own.</summary>
    public const string ResidentialStandard = "RES-STD";

    /// <summary>Code of the standard commercial tariff.</summary>
    public const string CommercialStandard = "COM-STD";

    /// <summary>
    /// The instant this reference set was authored, and the timestamp component of every plan and
    /// tier id. Fixed forever: changing it changes every id, which to the database is a different
    /// set of tariffs. A <i>new version</i> of a tariff does not need a new instant — the version
    /// key already carries its effective date (<see cref="RatePlan.KeyFor"/>).
    /// </summary>
    public static readonly DateTimeOffset AuthoredAt = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The day the shipped tariffs first applied.
    /// </summary>
    /// <remarks>
    /// Deliberately well before the demo world's oldest reading cycle. WP-0.8 dated these at
    /// 1 January 2026, which is <i>after</i> five of the twelve months <c>MeterReadingsDemoSeeder</c>
    /// lays down — so those cycles priced to nothing at all, correctly (a premise metered before its
    /// tariff existed cannot be billed) and uselessly. A utility's standing tariff predates the
    /// meter reads it is applied to; this is that, stated in the data.
    /// </remarks>
    public static readonly DateOnly OriginalEffectiveFrom = new(2025, 1, 1);

    /// <summary>
    /// The day the residential tariff was repriced. A mid-year revision on purpose: the demo world's
    /// twelve seeded reading cycles straddle it, so bills on both sides of it exist to compare.
    /// </summary>
    public static readonly DateOnly ResidentialRevisionFrom = new(2026, 7, 1);

    /// <summary>Every plan version, oldest first.</summary>
    public static IReadOnlyList<RatePlan> All { get; } =
    [
        RatePlan.Reference(
            ResidentialStandard,
            "Residential standard",
            ServiceType.Electricity,
            "USD",
            "kWh",
            monthlyServiceCharge: 12.50m,
            OriginalEffectiveFrom,
            isDefault: true),
        RatePlan.Reference(
            CommercialStandard,
            "Commercial standard",
            ServiceType.Electricity,
            "USD",
            "kWh",
            monthlyServiceCharge: 45.00m,
            OriginalEffectiveFrom,
            isDefault: false),

        // The revision. Still the default: the default is a tariff, and a tariff that is repriced is
        // still the one an account with no plan of its own is billed on.
        RatePlan.Reference(
            ResidentialStandard,
            "Residential standard",
            ServiceType.Electricity,
            "USD",
            "kWh",
            monthlyServiceCharge: 13.75m,
            ResidentialRevisionFrom,
            isDefault: true),
    ];

    /// <summary>Every tier of every plan version, validated as a set when this type is first touched.</summary>
    public static IReadOnlyList<RatePlanTier> AllTiers { get; } = BuildTiers();

    /// <summary>
    /// The code an <b>electricity</b> service account with no tariff of its own is billed on.
    /// </summary>
    /// <remarks>
    /// The unqualified default, kept because electricity is the one service the demonstration
    /// utility distributes and every consumption bill in GridCore is raised for it. A caller holding
    /// a service type wants <see cref="DefaultCodeFor"/> instead, which is the same answer here and
    /// a different one the day a water tariff ships.
    /// </remarks>
    public static string DefaultCode => ResidentialStandard;

    /// <summary>
    /// The code a service account taking <paramref name="serviceType"/> falls back to, or
    /// <see langword="null"/> where the utility publishes no default tariff for that service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A null is a real answer (WP-2.17).</b> The shipped schedule is entirely electricity, so
    /// water, gas and wastewater have no default and this says so rather than quietly billing a water
    /// account on the residential electric tariff. What the caller does with the null is its own
    /// business — <c>RatePlanService.ForAccountAsync</c> turns it into a refusal that names the
    /// billing-deepening pass, which is the package that owns unmetered and non-electric billing.
    /// </para>
    /// <para>
    /// Read off the shipped set rather than hard-coded per service, so adding a default water tariff
    /// is a migration and a row in <see cref="All"/> — never an edit here as well.
    /// </para>
    /// </remarks>
    public static string? DefaultCodeFor(ServiceType serviceType) =>
        All.Where(plan => plan.ServiceType == serviceType && plan.IsDefault)
            .Select(plan => plan.Code)
            .FirstOrDefault();

    /// <summary>Every version of <paramref name="code"/>, oldest first; empty if no such tariff ships.</summary>
    public static IReadOnlyList<RatePlan> VersionsOf(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        return [.. All.Where(plan => string.Equals(plan.Code, code, StringComparison.Ordinal)).OrderBy(plan => plan.EffectiveFrom)];
    }

    /// <summary>
    /// The version of <paramref name="code"/> in force on <paramref name="on"/>, or
    /// <see langword="null"/> if the tariff had not been published yet.
    /// </summary>
    public static RatePlan? InForceOn(string code, DateOnly on) => RatePlanSelector.InForceOn(VersionsOf(code), on);

    /// <summary>The version of the default tariff in force on <paramref name="on"/>.</summary>
    /// <exception cref="KeyNotFoundException">No default tariff had been published by then.</exception>
    public static RatePlan DefaultOn(DateOnly on) =>
        InForceOn(DefaultCode, on)
        ?? throw new KeyNotFoundException($"No default rate plan was in force on {on:yyyy-MM-dd}.");

    /// <summary>The <b>first</b> version of the plan with <paramref name="code"/>.</summary>
    /// <exception cref="KeyNotFoundException">No plan has that code.</exception>
    public static RatePlan Require(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        return VersionsOf(code).FirstOrDefault()
            ?? throw new KeyNotFoundException(
                $"'{code}' is not a rate plan GridCore ships. Plans are reference data; adding one is a migration.");
    }

    /// <summary>The tiers of <paramref name="version"/>, in order.</summary>
    public static IReadOnlyList<RatePlanTier> TiersOf(RatePlan version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return [.. AllTiers.Where(tier => tier.RatePlanId == version.Id).OrderBy(tier => tier.Sequence)];
    }

    /// <summary>The tiers of the <b>first</b> version of the plan with <paramref name="code"/>, in order.</summary>
    public static IReadOnlyList<RatePlanTier> TiersOf(string code) => TiersOf(Require(code));

    private static IReadOnlyList<RatePlanTier> BuildTiers()
    {
        var residential = All[0];
        var commercial = All[1];
        var repriced = All[2];

        // Inclining block: the first 500 kWh are the cheapest, and heavy use costs more per unit.
        RatePlanTier[] residentialTiers =
        [
            RatePlanTier.Reference(residential.VersionKey, residential.Id, 1, upToUnits: 500m, ratePerUnit: 0.1145m),
            RatePlanTier.Reference(residential.VersionKey, residential.Id, 2, upToUnits: 1_000m, ratePerUnit: 0.1385m),
            RatePlanTier.Reference(residential.VersionKey, residential.Id, 3, upToUnits: null, ratePerUnit: 0.1620m),
        ];

        // Declining block: volume is cheaper per unit, the usual shape of a commercial tariff.
        RatePlanTier[] commercialTiers =
        [
            RatePlanTier.Reference(commercial.VersionKey, commercial.Id, 1, upToUnits: 2_000m, ratePerUnit: 0.1290m),
            RatePlanTier.Reference(commercial.VersionKey, commercial.Id, 2, upToUnits: null, ratePerUnit: 0.1105m),
        ];

        // The revision keeps the block boundaries and moves the prices, which is what a repricing
        // usually is. Same shape, different rates — so a bill either side of 1 July differs by the
        // rate alone and the arithmetic is comparable.
        RatePlanTier[] repricedTiers =
        [
            RatePlanTier.Reference(repriced.VersionKey, repriced.Id, 1, upToUnits: 500m, ratePerUnit: 0.1225m),
            RatePlanTier.Reference(repriced.VersionKey, repriced.Id, 2, upToUnits: 1_000m, ratePerUnit: 0.1480m),
            RatePlanTier.Reference(repriced.VersionKey, repriced.Id, 3, upToUnits: null, ratePerUnit: 0.1735m),
        ];

        // Every shipped version is held to exactly the rules any other plan is. A malformed default
        // would otherwise be discovered by the first bill run rather than by the build.
        RatePlanTiers.Validate(residential.VersionKey, residentialTiers);
        RatePlanTiers.Validate(commercial.VersionKey, commercialTiers);
        RatePlanTiers.Validate(repriced.VersionKey, repricedTiers);

        return [.. residentialTiers, .. commercialTiers, .. repricedTiers];
    }
}
