using GridCore.Modules.Billing.Features.RatePlans;

namespace GridCore.Modules.Billing.UnitTests.RatePlans;

/// <summary>
/// The tariffs GridCore ships. They are reference data — a migrated database can bill without ever
/// being seeded — so they are held to exactly the rules any other plan is.
/// </summary>
public class DefaultRatePlansTests
{
    [Fact]
    public void Exactly_one_plan_is_the_default()
    {
        // "The default plan" cannot be two things. The database says so with a filtered unique
        // index; this says so before the migration ever runs.
        Assert.Single(DefaultRatePlans.All, plan => plan.IsDefault);
        Assert.Equal(DefaultRatePlans.ResidentialStandard, DefaultRatePlans.Default.Code);
    }

    [Fact]
    public void Plan_codes_and_ids_are_unique()
    {
        Assert.Equal(DefaultRatePlans.All.Count, DefaultRatePlans.All.Select(plan => plan.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(DefaultRatePlans.All.Count, DefaultRatePlans.All.Select(plan => plan.Id).Distinct().Count());
    }

    [Fact]
    public void Tier_ids_are_unique_across_every_plan()
    {
        // Tier ids are derived from the plan code and the sequence; a collision would mean the
        // migration inserted one tier and silently dropped another.
        Assert.Equal(DefaultRatePlans.AllTiers.Count, DefaultRatePlans.AllTiers.Select(tier => tier.Id).Distinct().Count());
    }

    [Fact]
    public void A_plan_id_is_the_same_every_time_the_set_is_built()
    {
        var rebuilt = RatePlan.Reference(
            DefaultRatePlans.ResidentialStandard,
            "Residential standard",
            ServiceType.Electricity,
            "USD",
            "kWh",
            monthlyServiceCharge: 12.50m,
            new DateOnly(2026, 1, 1),
            isDefault: true);

        Assert.Equal(rebuilt.Id, DefaultRatePlans.Default.Id);
    }

    [Fact]
    public void Every_shipped_plan_has_a_billable_set_of_tiers()
    {
        foreach (var plan in DefaultRatePlans.All)
        {
            RatePlanTiers.Validate(plan.Code, DefaultRatePlans.TiersOf(plan.Code));
        }
    }

    [Fact]
    public void Every_plan_has_tiers()
    {
        Assert.All(DefaultRatePlans.All, plan => Assert.NotEmpty(DefaultRatePlans.TiersOf(plan.Code)));
    }

    [Fact]
    public void The_residential_tariff_inclines_and_the_commercial_one_declines()
    {
        // Two shapes on purpose, so WP-2.3's rate engine has both to get right rather than one
        // shape that happens to work.
        var residential = DefaultRatePlans.TiersOf(DefaultRatePlans.ResidentialStandard);
        var commercial = DefaultRatePlans.TiersOf(DefaultRatePlans.CommercialStandard);

        Assert.Equal(
            residential.Select(tier => tier.RatePerUnit).OrderBy(rate => rate),
            residential.Select(tier => tier.RatePerUnit));

        Assert.Equal(
            commercial.Select(tier => tier.RatePerUnit).OrderByDescending(rate => rate),
            commercial.Select(tier => tier.RatePerUnit));
    }

    [Fact]
    public void Charges_and_rates_are_decimal_and_exact()
    {
        // Money is decimal, never a float (invariant 4). 12.50 must still be 12.50.
        Assert.Equal(12.50m, DefaultRatePlans.Default.MonthlyServiceCharge);
        Assert.Equal(0.1145m, DefaultRatePlans.TiersOf(DefaultRatePlans.ResidentialStandard)[0].RatePerUnit);
    }

    [Fact]
    public void Every_plan_names_an_ISO_currency_and_a_unit()
    {
        Assert.All(DefaultRatePlans.All, plan =>
        {
            Assert.Equal(RatePlan.CurrencyLength, plan.Currency.Length);
            Assert.NotEmpty(plan.UnitOfMeasure);
        });
    }

    [Fact]
    public void Asking_for_a_plan_that_does_not_exist_throws()
    {
        // Failure path: plans are reference data, so an unknown code is a mistake, not an empty
        // result to fall through on.
        Assert.Throws<KeyNotFoundException>(() => DefaultRatePlans.Require("NOPE"));
    }

    [Fact]
    public void A_negative_service_charge_is_refused()
    {
        // A credit dressed up as a tariff. Money goes back to a customer through an audited
        // adjustment (WP-2.4), never through a published rate.
        Assert.Throws<ArgumentException>(() => RatePlan.Reference(
            "BAD", "Negative", ServiceType.Electricity, "USD", "kWh", -1m, new DateOnly(2026, 1, 1), isDefault: false));
    }

    [Fact]
    public void A_currency_that_is_not_an_ISO_code_is_refused()
    {
        Assert.Throws<ArgumentException>(() => RatePlan.Reference(
            "BAD", "Dollars", ServiceType.Electricity, "DOLLARS", "kWh", 1m, new DateOnly(2026, 1, 1), isDefault: false));
    }
}
