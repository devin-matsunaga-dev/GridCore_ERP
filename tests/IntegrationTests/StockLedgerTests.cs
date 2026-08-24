using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Inventory.Data;
using GridCore.Modules.Inventory.Features.Items;
using GridCore.Modules.Inventory.Features.Warehouses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GridCore.IntegrationTests;

/// <summary>
/// The store against real Postgres. The fast tier proves the arithmetic, the guards and the ledger on
/// SQLite; what a container adds is the rules only the database can keep — the composite unique index
/// behind a race the service's own check cannot see, and the <c>numeric(18,3)</c> and
/// <c>numeric(18,2)</c> columns a quantity and a cost actually land in.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StockLedgerTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Stock_moves_across_warehouses_and_the_ledger_still_adds_up_to_the_levels()
    {
        var main = DefaultWarehouses.Require(DefaultWarehouses.LowerBase).Id;
        var depot = DefaultWarehouses.Require(DefaultWarehouses.Rota).Id;

        Guid itemId;

        await using (var scope = fixture.CreateScope())
        {
            itemId = (await scope.ServiceProvider.GetRequiredService<IStockItemService>()
                .RegisterAsync(new RegisterStockItemInput(
                    StockItemCategory.Conductor,
                    "ACSR Raven 1/0 conductor",
                    UnitOfMeasure.Metre,
                    "Bare overhead conductor",
                    "ACSR-RAVEN-1/0",
                    4.85m))).Id;
        }

        // Each movement on its own request, so what is asserted below is what Postgres holds rather
        // than what one change tracker remembers.
        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStockItemService>()
                .ReceiveAsync(itemId, new ReceiveStockInput(main, 2000.500m, 4.85m, "DN-4471"));
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStockItemService>()
                .ReceiveAsync(itemId, new ReceiveStockInput(depot, 600m, 4.85m, "TR-0112"));
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStockItemService>()
                .IssueAsync(itemId, new IssueStockInput(depot, 180.250m, Note: "As Nieves lateral rebuild, Rota"));
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStockItemService>()
                .AdjustAsync(itemId, new AdjustStockInput(main, 1990m, "Annual stock take: cut ends unaccounted for"));
        }

        await using var read = fixture.CreateScope();

        var stored = await read.ServiceProvider.GetRequiredService<InventoryDbContext>()
            .StockItems.AsNoTracking()
            .Include(item => item.Levels)
            .Include(item => item.Movements)
            .SingleAsync(item => item.Id == itemId);

        // Through numeric(18,3) and back, exact to the third place. On a float column this is where
        // 419.750 would come back as 419.74999999999994.
        Assert.Equal(1990m, stored.OnHandIn(main));
        Assert.Equal(419.750m, stored.OnHandIn(depot));
        Assert.Equal(2409.750m, stored.TotalOnHand);

        Assert.Equal(
            [
                StockMovementType.Receipt,
                StockMovementType.Receipt,
                StockMovementType.Issue,
                StockMovementType.Adjustment,
            ],
            stored.Movements.OrderBy(movement => movement.Id).Select(movement => movement.MovementType).ToArray());

        // Every shelf is exactly what its own lines add up to — the property that makes a count
        // answerable rather than merely current.
        Assert.All(stored.Levels, level =>
            Assert.Equal(
                stored.Movements
                    .Where(movement => movement.WarehouseId == level.WarehouseId)
                    .Sum(movement => movement.QuantityChange),
                level.QuantityOnHand));

        // The adjustment derived the shortfall rather than being handed it.
        var adjustment = stored.Movements.Single(movement => movement.MovementType == StockMovementType.Adjustment);

        Assert.Equal(-10.500m, adjustment.QuantityChange);
        Assert.Equal(4.85m, stored.Movements.First(movement => movement.UnitCost.HasValue).UnitCost);
    }

    [Fact]
    public async Task The_database_refuses_a_second_shelf_for_one_item_in_one_warehouse()
    {
        // The aggregate is the only thing that can open a level, so the service's check cannot see a
        // level another transaction is opening — which is exactly what two first deliveries racing
        // do. This inserts straight past that check, the way the race does. The composite unique
        // index is the only thing standing between it and one item holding two half-counts.
        var main = DefaultWarehouses.Require(DefaultWarehouses.LowerBase).Id;

        Guid itemId;

        await using (var scope = fixture.CreateScope())
        {
            var stock = scope.ServiceProvider.GetRequiredService<IStockItemService>();

            itemId = (await stock.RegisterAsync(new RegisterStockItemInput(
                StockItemCategory.Hardware, "LV connector kit, 4-way", UnitOfMeasure.Each))).Id;
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStockItemService>()
                .ReceiveAsync(itemId, new ReceiveStockInput(main, 120m));
        }

        await using var second = fixture.CreateScope();

        var database = second.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var failure = await Assert.ThrowsAsync<PostgresException>(() => database.Database.ExecuteSqlRawAsync(
            """
            insert into inventory.stock_levels (id, stock_item_id, warehouse_id, quantity_on_hand, minimum_quantity)
            values ({0}, {1}, {2}, 0, 0)
            """,
            Guid.CreateVersion7(DateTimeOffset.UtcNow),
            itemId,
            main));

        Assert.Equal("23505", failure.SqlState);
    }

    [Fact]
    public async Task The_database_refuses_a_second_catalogue_line_for_one_part_number()
    {
        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStockItemService>()
                .RegisterAsync(new RegisterStockItemInput(
                    StockItemCategory.Hardware,
                    "LV connector kit, 4-way",
                    UnitOfMeasure.Each,
                    ManufacturerPartNumber: "TE-LV4-CONN"));
        }

        await using var second = fixture.CreateScope();

        var database = second.ServiceProvider.GetRequiredService<InventoryDbContext>();

        database.StockItems.Add(StockItem.Register(
            "ITM-999999",
            StockItemCategory.Hardware,
            "The same part, catalogued twice",
            UnitOfMeasure.Each,
            DateTimeOffset.UtcNow,
            manufacturerPartNumber: "TE-LV4-CONN"));

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());

        Assert.Equal("23505", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }

    [Fact]
    public async Task A_stock_level_cannot_name_a_warehouse_that_does_not_exist()
    {
        // A warehouse is reference data in this module's own schema, so unlike the work-order id on a
        // movement this one is a constraint the database keeps. The service answers 404 first; this
        // is what is underneath it.
        await using var scope = fixture.CreateScope();

        var database = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var item = StockItem.Register("ITM-999998", StockItemCategory.Hardware, "Something", UnitOfMeasure.Each, DateTimeOffset.UtcNow);

        item.Receive(Guid.CreateVersion7(DateTimeOffset.UtcNow), 1m, new("system", "system"), DateTimeOffset.UtcNow);

        database.StockItems.Add(item);

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());

        Assert.Equal("23503", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }
}
