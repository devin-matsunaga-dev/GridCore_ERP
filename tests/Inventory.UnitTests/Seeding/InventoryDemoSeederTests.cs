using GridCore.Modules.Inventory.Features.Items;
using GridCore.Modules.Inventory.Features.Warehouses;
using GridCore.Modules.Inventory.Seeding;
using GridCore.Modules.Inventory.UnitTests.Infrastructure;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Inventory.UnitTests.Seeding;

/// <summary>
/// The demo store. Seeded through the real movement methods, so these tests are also what prove a
/// demo world cannot ship a count nothing explains.
/// </summary>
public class InventoryDemoSeederTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static async Task<InventoryTestDatabase> SeededAsync()
    {
        var database = new InventoryTestDatabase();

        await new InventoryDemoSeeder(database.Context, new FakeClock(Now)).SeedAsync(CancellationToken.None);

        // The seeder never saves — the runner's unit of work does, in the same transaction as the
        // seed record. This stands in for that.
        await database.Context.SaveChangesAsync();

        return database;
    }

    [Fact]
    public void The_seeder_names_itself_and_runs_after_the_registries_it_follows()
    {
        IDemoSeeder seeder = new InventoryDemoSeeder(null!, TimeProvider.System);

        Assert.Equal("inventory.stock", seeder.Name);
        Assert.Equal(500, seeder.Order);
    }

    [Fact]
    public async Task Every_seeded_line_gets_a_code_in_one_unbroken_series()
    {
        using var database = await SeededAsync();

        await using var read = database.NewContext();

        var codes = await read.StockItems.OrderBy(item => item.ItemCode).Select(item => item.ItemCode).ToListAsync();

        // Assigned here rather than by the generator, which cannot see rows the seeding transaction
        // has not committed. Starting at 1 is what lets the first real registration continue it.
        Assert.Equal(
            Enumerable.Range(1, codes.Count).Select(ordinal => $"ITM-{ordinal:D6}"),
            codes);
    }

    [Fact]
    public async Task Every_seeded_level_is_exactly_what_its_ledger_adds_up_to()
    {
        // The property that makes the demo store answerable rather than merely populated — and the
        // one a hand-assigned quantity would quietly break.
        using var database = await SeededAsync();

        await using var read = database.NewContext();

        var levels = await read.StockLevels.AsNoTracking().ToListAsync();
        var movements = await read.StockMovements.AsNoTracking().ToListAsync();

        Assert.NotEmpty(levels);

        Assert.All(levels, level =>
            Assert.Equal(
                movements
                    .Where(movement => movement.StockItemId == level.StockItemId && movement.WarehouseId == level.WarehouseId)
                    .Sum(movement => movement.QuantityChange),
                level.QuantityOnHand));
    }

    [Fact]
    public async Task Every_ledger_line_says_who_moved_it_and_it_is_never_a_real_person()
    {
        using var database = await SeededAsync();

        await using var read = database.NewContext();

        var actors = await read.StockMovements.Select(movement => movement.ActorId).Distinct().ToListAsync();

        // The demo: prefix cannot collide with an identity-provider subject, and the actor holds no
        // permissions at all — so a seeded movement can never be mistaken for one a storeman made.
        Assert.Equal([InventoryDemoSeeder.Storeman.UserId], actors);
        Assert.StartsWith(DemoActor.IdPrefix, InventoryDemoSeeder.Storeman.UserId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_seeded_store_holds_stock_in_every_shipped_warehouse()
    {
        using var database = await SeededAsync();

        await using var read = database.NewContext();

        var stocked = await read.StockLevels.Select(level => level.WarehouseId).Distinct().ToListAsync();

        Assert.Equal(
            DefaultWarehouses.All.Select(warehouse => warehouse.Id).Order(),
            stocked.Order());
    }

    [Fact]
    public async Task Every_category_and_every_movement_type_is_demonstrated()
    {
        using var database = await SeededAsync();

        await using var read = database.NewContext();

        var categories = await read.StockItems.Select(item => item.Category).Distinct().ToListAsync();

        Assert.Equal(Enum.GetValues<StockItemCategory>().Order(), categories.Order());

        var types = await read.StockMovements.Select(movement => movement.MovementType).Distinct().ToListAsync();

        // Including an adjustment: without one the demo cannot show the sensitive path at all.
        Assert.Equal(Enum.GetValues<StockMovementType>().Order(), types.Order());
    }

    [Fact]
    public async Task The_awkward_states_a_screen_has_to_render_are_all_present()
    {
        using var database = await SeededAsync();

        await using var read = database.NewContext();

        var items = await read.StockItems
            .Include(item => item.Levels)
            .AsNoTracking()
            .ToListAsync();

        // A shelf below its reorder level, for the low-stock report.
        Assert.Contains(items, item => item.Levels.Any(level => level.IsBelowMinimum));

        // A healthy shelf, so the report is not simply everything.
        Assert.Contains(items, item => item.Levels.Any(level => level.MinimumQuantity > 0 && !level.IsBelowMinimum));

        // A line on order with nothing delivered yet.
        Assert.Contains(items, item => item.TotalOnHand == 0m && item.Levels.Count > 0);

        // A discontinued line with a remainder still to be used up, and a reason on the record.
        var discontinued = Assert.Single(items, item => !item.IsActive);

        Assert.True(discontinued.TotalOnHand > 0m);
        Assert.False(string.IsNullOrWhiteSpace(discontinued.StatusReason));

        // An item held in more than one warehouse.
        Assert.Contains(items, item => item.Levels.Count > 1);
    }

    [Fact]
    public async Task The_stock_take_left_less_than_the_delivery_did()
    {
        using var database = await SeededAsync();

        await using var read = database.NewContext();

        var adjustment = await read.StockMovements.SingleAsync(movement => movement.MovementType == StockMovementType.Adjustment);

        Assert.True(adjustment.QuantityChange < 0m);
        Assert.False(string.IsNullOrWhiteSpace(adjustment.Note));
    }

    [Fact]
    public async Task Seeded_places_are_the_three_islands_rather_than_invented_districts()
    {
        // The owner's standing note about place names, applied to seeded data as WP-1.1 and WP-1.3
        // applied it: the demo utility is Rota Utilities, so its stock moves on Rota, Saipan and
        // Tinian.
        using var database = await SeededAsync();

        await using var read = database.NewContext();

        var notes = string.Join(" ", await read.StockMovements.Select(movement => movement.Note).ToListAsync());

        Assert.Contains("Rota", notes, StringComparison.Ordinal);
        Assert.Contains("Saipan", notes, StringComparison.Ordinal);
        Assert.Contains("Tinian", notes, StringComparison.Ordinal);
    }
}
