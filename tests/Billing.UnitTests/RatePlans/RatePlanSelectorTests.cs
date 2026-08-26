using GridCore.Contracts.Services;
using GridCore.Modules.Billing.Features.RatePlans;

namespace GridCore.Modules.Billing.UnitTests.RatePlans;

/// <summary>
/// Effective dating: which version of a tariff applies on a given day. Pure, so every case is
/// provable with no database — and the cases matter, because getting this wrong reprices bills that
/// have already been sent.
/// </summary>
public class RatePlanSelectorTests
{
    private static RatePlan Version(string code, DateOnly from, decimal charge = 10m) =>
        RatePlan.Reference(code, code, ServiceType.Electricity, "USD", "kWh", charge, from, isDefault: false);

    private static readonly RatePlan January = Version("RES", new DateOnly(2026, 1, 1), 12.50m);
    private static readonly RatePlan July = Version("RES", new DateOnly(2026, 7, 1), 13.75m);
    private static readonly RatePlan NextYear = Version("RES", new DateOnly(2027, 1, 1), 15.00m);

    private static readonly RatePlan[] AllVersions = [July, NextYear, January];

    [Theory]

    // The day before the first version exists: no answer at all.
    [InlineData("2025-12-31", null)]

    // The day it takes effect, and every day up to the one before the next.
    [InlineData("2026-01-01", "2026-01-01")]
    [InlineData("2026-06-30", "2026-01-01")]

    // The revision, from its own first day.
    [InlineData("2026-07-01", "2026-07-01")]
    [InlineData("2026-12-31", "2026-07-01")]
    [InlineData("2027-01-01", "2027-01-01")]
    [InlineData("2030-05-04", "2027-01-01")]
    public void The_version_in_force_is_the_latest_one_that_had_taken_effect(string on, string? expected)
    {
        var inForce = RatePlanSelector.InForceOn(AllVersions, DateOnly.Parse(on, null));

        Assert.Equal(expected is null ? null : DateOnly.Parse(expected, null), inForce?.EffectiveFrom);
    }

    [Fact]
    public void A_version_applies_from_its_own_first_day_and_not_before() =>
        // The boundary, stated twice because it is the one an off-by-one lands on: 30 June is the
        // old rates and 1 July is the new ones.
        Assert.Equal(
            [January.Id, July.Id],
            new[] { new DateOnly(2026, 6, 30), new DateOnly(2026, 7, 1) }
                .Select(on => RatePlanSelector.InForceOn(AllVersions, on)!.Id));

    [Fact]
    public void The_order_the_versions_arrive_in_does_not_matter() =>
        // They come back from an Include in whatever order the database chose. Trusting that order
        // would make the answer depend on the query plan.
        Assert.All(
            new[] { AllVersions, [January, July, NextYear], [NextYear, July, January] },
            versions => Assert.Equal(
                July.Id,
                RatePlanSelector.InForceOn(versions, new DateOnly(2026, 8, 15))!.Id));

    [Fact]
    public void Versions_of_other_tariffs_are_ignored_rather_than_trusted()
    {
        // A caller holding every plan in the database must not be handed a commercial tariff
        // because it happened to be published later.
        var everything = new[] { January, July, Version("COM", new DateOnly(2026, 9, 1)) };

        Assert.Equal(July.Id, RatePlanSelector.InForceOn(everything, "RES", new DateOnly(2026, 10, 1))!.Id);
        Assert.Equal("COM", RatePlanSelector.InForceOn(everything, "COM", new DateOnly(2026, 10, 1))!.Code);
    }

    [Fact]
    public void A_tariff_that_had_not_been_published_yet_resolves_to_nothing()
    {
        // Failure path, and a real answer rather than an exception: a premise metered before its
        // tariff existed cannot be billed, and saying so beats billing it on rates that were not
        // published at the time.
        Assert.Null(RatePlanSelector.InForceOn(AllVersions, new DateOnly(2025, 1, 1)));
        Assert.Null(RatePlanSelector.InForceOn([], new DateOnly(2026, 8, 1)));
        Assert.Null(RatePlanSelector.InForceOn(AllVersions, "NOPE", new DateOnly(2026, 8, 1)));
    }

    [Fact]
    public void The_shipped_residential_tariff_switches_on_the_first_of_July()
    {
        // The same rule against the rows that actually ship, so a repricing that moved would fail
        // here rather than quietly change every seeded bill.
        var versions = DefaultRatePlans.VersionsOf(DefaultRatePlans.ResidentialStandard);

        Assert.Equal(12.50m, DefaultRatePlans.DefaultOn(new DateOnly(2026, 6, 30)).MonthlyServiceCharge);
        Assert.Equal(13.75m, DefaultRatePlans.DefaultOn(new DateOnly(2026, 7, 1)).MonthlyServiceCharge);
        Assert.Equal(versions[1].Id, DefaultRatePlans.DefaultOn(new DateOnly(2026, 8, 1)).Id);
    }

    [Fact]
    public void The_commercial_tariff_has_only_ever_had_one_version() =>
        Assert.Equal(
            DefaultRatePlans.Require(DefaultRatePlans.CommercialStandard).Id,
            DefaultRatePlans.InForceOn(DefaultRatePlans.CommercialStandard, new DateOnly(2030, 1, 1))!.Id);
}
