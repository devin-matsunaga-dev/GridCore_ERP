using GridCore.Modules.Billing.Features.RatePlans;

namespace GridCore.Modules.Billing.UnitTests.RatePlans;

/// <summary>
/// The tariffs GridCore ships. They are reference data — a migrated database can bill without ever
/// being seeded — so they are held to exactly the rules any other plan is.
/// </summary>
public class DefaultRatePlansTests
{
    [Fact]
    public void Exactly_one_plan_is_the_default_on_any_given_day()
    {
        // "The default plan" cannot be two things AT ONCE. The database says so with a filtered
        // unique index on (is_default, effective_from); this says so before the migration runs.
        // Every version of the default tariff carries the flag, so repricing it does not leave the
        // utility without one — which is why the assertion is per date rather than per set.
        foreach (var on in DefaultRatePlans.All.Select(plan => plan.EffectiveFrom).Distinct())
        {
            Assert.Single(DefaultRatePlans.All, plan => plan.IsDefault && plan.EffectiveFrom == on);
        }

        Assert.Equal(DefaultRatePlans.ResidentialStandard, DefaultRatePlans.DefaultCode);
    }

    [Fact]
    public void A_code_is_not_an_identity_but_a_code_and_a_date_is()
    {
        // The residential tariff ships twice. What must be unique is the version — the pair — and
        // that is what the id is derived from; unique on the code alone would make repricing
        // impossible, which is what WP-0.8 shipped and WP-2.3 had to change.
        Assert.Equal(2, DefaultRatePlans.VersionsOf(DefaultRatePlans.ResidentialStandard).Count);

        Assert.Equal(
            DefaultRatePlans.All.Count,
            DefaultRatePlans.All.Select(plan => plan.VersionKey).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(DefaultRatePlans.All.Count, DefaultRatePlans.All.Select(plan => plan.Id).Distinct().Count());
    }

    [Fact]
    public void Tier_ids_are_unique_across_every_plan_version()
    {
        // Tier ids are derived from the plan VERSION key and the sequence. Derived from the code
        // alone, the two residential versions' tier 1s would collide and the migration would insert
        // one and silently drop the other — which is exactly what happened before this WP rekeyed
        // them.
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
            DefaultRatePlans.OriginalEffectiveFrom,
            isDefault: true);

        Assert.Equal(rebuilt.Id, DefaultRatePlans.VersionsOf(DefaultRatePlans.ResidentialStandard)[0].Id);
    }

    [Fact]
    public void Every_shipped_version_has_a_billable_set_of_tiers()
    {
        foreach (var plan in DefaultRatePlans.All)
        {
            RatePlanTiers.Validate(plan.VersionKey, DefaultRatePlans.TiersOf(plan));
        }
    }

    [Fact]
    public void Every_version_has_tiers() =>
        Assert.All(DefaultRatePlans.All, plan => Assert.NotEmpty(DefaultRatePlans.TiersOf(plan)));

    [Fact]
    public void The_residential_tariff_inclines_and_the_commercial_one_declines()
    {
        // Two shapes on purpose, so the rate engine has both to get right rather than one shape
        // that happens to work.
        var commercial = DefaultRatePlans.TiersOf(DefaultRatePlans.CommercialStandard);

        foreach (var version in DefaultRatePlans.VersionsOf(DefaultRatePlans.ResidentialStandard))
        {
            var tiers = DefaultRatePlans.TiersOf(version);

            Assert.Equal(tiers.Select(tier => tier.RatePerUnit).OrderBy(rate => rate), tiers.Select(tier => tier.RatePerUnit));
        }

        Assert.Equal(
            commercial.Select(tier => tier.RatePerUnit).OrderByDescending(rate => rate),
            commercial.Select(tier => tier.RatePerUnit));
    }

    [Fact]
    public void The_revision_costs_more_than_the_version_it_replaced()
    {
        // Not decoration: a repricing that happened to charge the same would make every
        // effective-dating test pass whichever version it picked.
        var versions = DefaultRatePlans.VersionsOf(DefaultRatePlans.ResidentialStandard);

        Assert.True(versions[1].EffectiveFrom > versions[0].EffectiveFrom);
        Assert.True(versions[1].MonthlyServiceCharge > versions[0].MonthlyServiceCharge);

        Assert.All(
            DefaultRatePlans.TiersOf(versions[1]).Zip(DefaultRatePlans.TiersOf(versions[0])),
            pair => Assert.True(pair.First.RatePerUnit > pair.Second.RatePerUnit));
    }

    [Fact]
    public void Charges_and_rates_are_decimal_and_exact()
    {
        // Money is decimal, never a float (invariant 4). 12.50 must still be 12.50.
        var original = DefaultRatePlans.VersionsOf(DefaultRatePlans.ResidentialStandard)[0];

        Assert.Equal(12.50m, original.MonthlyServiceCharge);
        Assert.Equal(0.1145m, DefaultRatePlans.TiersOf(original)[0].RatePerUnit);
    }

    [Fact]
    public void Every_plan_names_an_ISO_currency_and_a_unit() =>
        Assert.All(DefaultRatePlans.All, plan =>
        {
            Assert.Equal(RatePlan.CurrencyLength, plan.Currency.Length);
            Assert.NotEmpty(plan.UnitOfMeasure);
        });

    [Fact]
    public void Asking_for_a_plan_that_does_not_exist_throws() =>
        // Failure path: plans are reference data, so an unknown code is a mistake, not an empty
        // result to fall through on.
        Assert.Throws<KeyNotFoundException>(() => DefaultRatePlans.Require("NOPE"));

    [Fact]
    public void Asking_for_the_default_before_any_tariff_existed_throws() =>
        // Failure path for effective dating itself: a bill for a period before the utility had
        // published a tariff cannot be priced, and saying so beats inventing rates.
        Assert.Throws<KeyNotFoundException>(() =>
            DefaultRatePlans.DefaultOn(DefaultRatePlans.OriginalEffectiveFrom.AddDays(-1)));

    [Fact]
    public void A_negative_service_charge_is_refused() =>
        // A credit dressed up as a tariff. Money goes back to a customer through an audited
        // adjustment (WP-2.4), never through a published rate.
        Assert.Throws<ArgumentException>(() => RatePlan.Reference(
            "BAD", "Negative", ServiceType.Electricity, "USD", "kWh", -1m, new DateOnly(2026, 1, 1), isDefault: false));

    [Fact]
    public void A_currency_that_is_not_an_ISO_code_is_refused() =>
        Assert.Throws<ArgumentException>(() => RatePlan.Reference(
            "BAD", "Dollars", ServiceType.Electricity, "DOLLARS", "kWh", 1m, new DateOnly(2026, 1, 1), isDefault: false));
}
