using GridCore.Modules.Billing.Features.RatePlans;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.UnitTests.RatePlans;

/// <summary>
/// The billing schema as EF actually builds it. These assertions prove the other half of
/// ARCHITECTURE.md invariant 8: a database that has only been migrated can already bill.
/// </summary>
public class BillingModelTests
{
    [Fact]
    public async Task Creating_the_schema_seeds_the_shipped_tariffs_and_their_tiers()
    {
        using var database = new BillingTestDatabase();

        await using var context = database.NewContext();

        Assert.Equal(DefaultRatePlans.All.Count, await context.RatePlans.CountAsync());
        Assert.Equal(DefaultRatePlans.AllTiers.Count, await context.RatePlanTiers.CountAsync());
    }

    [Fact]
    public async Task The_seeded_default_plan_loads_with_its_tiers_in_order()
    {
        using var database = new BillingTestDatabase();

        await using var context = database.NewContext();

        var plan = await context.RatePlans
            .Include(plan => plan.Tiers)
            .SingleAsync(plan => plan.IsDefault && plan.EffectiveFrom == DefaultRatePlans.OriginalEffectiveFrom);

        Assert.Equal(DefaultRatePlans.ResidentialStandard, plan.Code);
        Assert.Equal(12.50m, plan.MonthlyServiceCharge);

        var tiers = plan.Tiers.OrderBy(tier => tier.Sequence).ToList();

        Assert.Equal([1, 2, 3], tiers.Select(tier => tier.Sequence));
        Assert.Null(tiers[^1].UpToUnits);
    }

    [Fact]
    public async Task A_second_default_plan_on_the_same_day_is_refused_by_the_database()
    {
        // Failure path: the filtered unique index is what makes "the default" a fact rather than a
        // convention every future query would have to hope held. Keyed on the effective date as
        // well, so a repriced default is legal and two defaults on one day are not.
        using var database = new BillingTestDatabase();

        database.Context.RatePlans.Add(RatePlan.Reference(
            "RES-ALT",
            "A second default",
            ServiceType.Electricity,
            "USD",
            "kWh",
            10m,
            DefaultRatePlans.OriginalEffectiveFrom,
            isDefault: true));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task Republishing_a_tariff_on_a_new_date_is_allowed()
    {
        // The other side of the index. A tariff whose prices change is the same tariff, and both
        // versions have to coexist — last July's bill is still billed on last July's rates.
        using var database = new BillingTestDatabase();

        database.Context.RatePlans.Add(RatePlan.Reference(
            DefaultRatePlans.CommercialStandard,
            "Commercial standard",
            ServiceType.Electricity,
            "USD",
            "kWh",
            50m,
            new DateOnly(2027, 1, 1),
            isDefault: false));

        await database.Context.SaveChangesAsync();

        await using var context = database.NewContext();

        Assert.Equal(2, await context.RatePlans.CountAsync(plan => plan.Code == DefaultRatePlans.CommercialStandard));
    }

    [Fact]
    public void The_same_tariff_cannot_take_effect_twice_on_one_day() =>
        // Not merely refused by the unique index — unreachable. A version's id is derived from its
        // code and its effective date, so a second row for the same pair is the same row.
        Assert.Equal(
            RatePlan.Reference(
                DefaultRatePlans.CommercialStandard,
                "Commercial standard",
                ServiceType.Electricity,
                "USD",
                "kWh",
                45m,
                DefaultRatePlans.OriginalEffectiveFrom,
                isDefault: false).Id,
            RatePlan.Reference(
                DefaultRatePlans.CommercialStandard,
                "A different name, same version",
                ServiceType.Electricity,
                "USD",
                "kWh",
                99m,
                DefaultRatePlans.OriginalEffectiveFrom,
                isDefault: false).Id);
}
