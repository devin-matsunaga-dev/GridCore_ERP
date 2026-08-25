using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Finance.Data;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Inventory.Data;
using GridCore.Modules.Inventory.Features.Warehouses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Chart = GridCore.Modules.Finance.Features.ChartOfAccounts.ChartOfAccounts;

namespace GridCore.IntegrationTests;

/// <summary>
/// ARCHITECTURE.md invariant 8's first half, against real Postgres: a database that has only been
/// migrated — never seeded — already holds everything the application needs to work. The fast tier
/// proves the same models on SQLite; what only a container can show is that the migrations really
/// carry the rows, and that wiping demo data between tests does not take reference data with it.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReferenceDataTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_migrated_database_holds_the_whole_chart_of_accounts()
    {
        await using var scope = fixture.CreateScope();

        var finance = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();

        var codes = await finance.Accounts.Select(account => account.Code).ToListAsync();

        Assert.Equal(
            Chart.All.Select(account => account.Code).Order(StringComparer.Ordinal),
            codes.Order(StringComparer.Ordinal));

        // The loop WP-0.5 opened with placeholder codes: the seam's accounts are real rows now.
        Assert.Contains(FinanceAccounts.AccountsReceivable, codes);
        Assert.Contains(FinanceAccounts.Revenue, codes);
    }

    [Fact]
    public async Task The_migrated_database_can_bill_and_can_hold_stock()
    {
        await using var scope = fixture.CreateScope();

        var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        // The default tariff is now a tariff with two published versions, so "the default" is a
        // question with a date on it (WP-2.3). Both are seeded; the one in force today is the later.
        var versions = await billing.RatePlans
            .Include(plan => plan.Tiers)
            .Where(plan => plan.IsDefault)
            .ToListAsync();

        Assert.Equal(2, versions.Count);
        Assert.All(versions, version => Assert.Equal(DefaultRatePlans.ResidentialStandard, version.Code));

        var plan = RatePlanSelector.InForceOn(versions, DefaultRatePlans.ResidentialRevisionFrom)!;

        Assert.Equal(13.75m, plan.MonthlyServiceCharge);
        Assert.Equal(3, plan.Tiers.Count);

        // And the version before the repricing is still there, still charging what it charged.
        Assert.Equal(
            12.50m,
            RatePlanSelector.InForceOn(versions, DefaultRatePlans.ResidentialRevisionFrom.AddDays(-1))!.MonthlyServiceCharge);

        Assert.Equal(
            DefaultWarehouses.All.Select(warehouse => warehouse.Code).Order(StringComparer.Ordinal),
            await inventory.Warehouses.Select(warehouse => warehouse.Code).OrderBy(code => code).ToListAsync());
    }

    [Fact]
    public async Task Wiping_demo_data_between_tests_does_not_wipe_reference_data()
    {
        // The failure this guards against is quiet and total: Respawn truncates, only a migration
        // puts reference rows back, and that migration is already recorded as applied — so the
        // second test in the run would find an empty chart of accounts and blame itself.
        await fixture.ResetAsync();

        await using var scope = fixture.CreateScope();

        var finance = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        Assert.Equal(Chart.All.Count, await finance.Accounts.CountAsync());
        Assert.Equal(DefaultRatePlans.All.Count, await billing.RatePlans.CountAsync());
        Assert.Equal(DefaultRatePlans.AllTiers.Count, await billing.RatePlanTiers.CountAsync());
        Assert.Equal(DefaultWarehouses.All.Count, await inventory.Warehouses.CountAsync());
    }
}
