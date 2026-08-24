namespace GridCore.Modules.Billing.Features.RatePlans;

/// <summary>
/// The tariffs the utility ships with: reference data, not demo data. A database that has been
/// migrated can bill (ARCHITECTURE.md invariant 8), whether or not anyone ever seeds a demo world.
/// </summary>
/// <remarks>
/// Two plans, because one is not enough to show that a service account <i>has</i> a tariff rather
/// than the system having one. Residential is inclining-block (heavier use costs more per unit) and
/// commercial is declining-block, which is what real electricity tariffs look like and gives WP-2.3's
/// rate engine two shapes to get right. Adding or changing a plan is a new migration — the rows are
/// seeded by one, and migrations are append-only (invariant 7).
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
    /// set of tariffs.
    /// </summary>
    public static readonly DateTimeOffset AuthoredAt = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly EffectiveFrom = new(2026, 1, 1);

    /// <summary>Every plan.</summary>
    public static IReadOnlyList<RatePlan> All { get; } =
    [
        RatePlan.Reference(
            ResidentialStandard,
            "Residential standard",
            ServiceType.Electricity,
            "USD",
            "kWh",
            monthlyServiceCharge: 12.50m,
            EffectiveFrom,
            isDefault: true),
        RatePlan.Reference(
            CommercialStandard,
            "Commercial standard",
            ServiceType.Electricity,
            "USD",
            "kWh",
            monthlyServiceCharge: 45.00m,
            EffectiveFrom,
            isDefault: false),
    ];

    /// <summary>Every tier of every plan, validated as a set when this type is first touched.</summary>
    public static IReadOnlyList<RatePlanTier> AllTiers { get; } = BuildTiers();

    /// <summary>The plan a service account with no tariff of its own is billed on.</summary>
    public static RatePlan Default => All.Single(plan => plan.IsDefault);

    /// <summary>The plan with <paramref name="code"/>.</summary>
    /// <exception cref="KeyNotFoundException">No plan has that code.</exception>
    public static RatePlan Require(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        return All.SingleOrDefault(plan => string.Equals(plan.Code, code, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"'{code}' is not a rate plan GridCore ships. Plans are reference data; adding one is a migration.");
    }

    /// <summary>The tiers of the plan with <paramref name="code"/>, in order.</summary>
    public static IReadOnlyList<RatePlanTier> TiersOf(string code)
    {
        var plan = Require(code);

        return [.. AllTiers.Where(tier => tier.RatePlanId == plan.Id).OrderBy(tier => tier.Sequence)];
    }

    private static IReadOnlyList<RatePlanTier> BuildTiers()
    {
        var residential = Require(ResidentialStandard);
        var commercial = Require(CommercialStandard);

        // Inclining block: the first 500 kWh are the cheapest, and heavy use costs more per unit.
        RatePlanTier[] residentialTiers =
        [
            RatePlanTier.Reference(residential.Code, residential.Id, 1, upToUnits: 500m, ratePerUnit: 0.1145m),
            RatePlanTier.Reference(residential.Code, residential.Id, 2, upToUnits: 1_000m, ratePerUnit: 0.1385m),
            RatePlanTier.Reference(residential.Code, residential.Id, 3, upToUnits: null, ratePerUnit: 0.1620m),
        ];

        // Declining block: volume is cheaper per unit, the usual shape of a commercial tariff.
        RatePlanTier[] commercialTiers =
        [
            RatePlanTier.Reference(commercial.Code, commercial.Id, 1, upToUnits: 2_000m, ratePerUnit: 0.1290m),
            RatePlanTier.Reference(commercial.Code, commercial.Id, 2, upToUnits: null, ratePerUnit: 0.1105m),
        ];

        // The shipped tariffs are held to exactly the rules any other plan is. A malformed default
        // would otherwise be discovered by the first bill run rather than by the build.
        RatePlanTiers.Validate(residential.Code, residentialTiers);
        RatePlanTiers.Validate(commercial.Code, commercialTiers);

        return [.. residentialTiers, .. commercialTiers];
    }
}
