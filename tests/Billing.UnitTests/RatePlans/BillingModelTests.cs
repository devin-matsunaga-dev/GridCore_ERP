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
            .SingleAsync(plan => plan.IsDefault);

        Assert.Equal(DefaultRatePlans.ResidentialStandard, plan.Code);
        Assert.Equal(12.50m, plan.MonthlyServiceCharge);

        var tiers = plan.Tiers.OrderBy(tier => tier.Sequence).ToList();

        Assert.Equal([1, 2, 3], tiers.Select(tier => tier.Sequence));
        Assert.Null(tiers[^1].UpToUnits);
    }

    [Fact]
    public async Task A_second_default_plan_is_refused_by_the_database()
    {
        // Failure path: the filtered unique index is what makes "the default" a fact rather than a
        // convention every future query would have to hope held.
        using var database = new BillingTestDatabase();

        database.Context.RatePlans.Add(RatePlan.Reference(
            "RES-ALT",
            "A second default",
            ServiceType.Electricity,
            "USD",
            "kWh",
            10m,
            new DateOnly(2026, 1, 1),
            isDefault: true));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }
}
