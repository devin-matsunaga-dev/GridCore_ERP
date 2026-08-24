using GridCore.Modules.Inventory.Features.Items;
using GridCore.Modules.Inventory.UnitTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GridCore.Modules.Inventory.UnitTests.Items;

/// <summary>
/// The real EF model, on SQLite in-memory. What is asserted here is the mapping a migration turns
/// into columns and constraints — the things a test against the aggregate alone cannot see.
/// </summary>
public class StockItemModelTests
{
    private static IEntityType EntityType<TEntity>()
    {
        using var database = new InventoryTestDatabase();

        return database.Context.Model.FindEntityType(typeof(TEntity))!;
    }

    [Fact]
    public void The_stock_tables_are_snake_case_in_the_inventory_schema()
    {
        Assert.Equal("stock_items", EntityType<StockItem>().GetTableName());
        Assert.Equal("stock_levels", EntityType<StockLevel>().GetTableName());
        Assert.Equal("stock_movements", EntityType<StockMovement>().GetTableName());

        Assert.All(
            new[] { EntityType<StockItem>(), EntityType<StockLevel>(), EntityType<StockMovement>() },
            entity => Assert.Equal("inventory", entity.GetSchema()));
    }

    [Fact]
    public void Every_id_is_minted_in_code_rather_than_by_the_store()
    {
        // Load-bearing, not cosmetic: with a store-generated key EF tracks a freshly appended ledger
        // line as Modified, and the save fails having updated nothing (WP-1.2's half hour).
        Assert.All(
            new[] { EntityType<StockItem>(), EntityType<StockLevel>(), EntityType<StockMovement>() },
            entity => Assert.Equal(ValueGenerated.Never, entity.FindProperty("Id")!.ValueGenerated));
    }

    [Fact]
    public void Quantities_are_decimal_to_three_places_and_money_to_two()
    {
        var level = EntityType<StockLevel>();

        Assert.Equal(StockQuantities.DecimalPlaces, level.FindProperty(nameof(StockLevel.QuantityOnHand))!.GetScale());
        Assert.Equal(StockQuantities.DecimalPlaces, level.FindProperty(nameof(StockLevel.MinimumQuantity))!.GetScale());

        var movement = EntityType<StockMovement>();

        Assert.Equal(StockQuantities.DecimalPlaces, movement.FindProperty(nameof(StockMovement.QuantityChange))!.GetScale());
        Assert.Equal(StockCosts.DecimalPlaces, movement.FindProperty(nameof(StockMovement.UnitCost))!.GetScale());

        // Money is decimal, never a float (invariant 4).
        Assert.Equal(typeof(decimal), EntityType<StockItem>().FindProperty(nameof(StockItem.UnitCost))!.ClrType);
    }

    [Fact]
    public void Categories_units_and_movement_types_are_stored_by_name()
    {
        // A record read years from now must not depend on today's enum ordering.
        Assert.Equal(typeof(string), EntityType<StockItem>().FindProperty(nameof(StockItem.Category))!.GetProviderClrType());
        Assert.Equal(typeof(string), EntityType<StockItem>().FindProperty(nameof(StockItem.Unit))!.GetProviderClrType());
        Assert.Equal(typeof(string), EntityType<StockMovement>().FindProperty(nameof(StockMovement.MovementType))!.GetProviderClrType());
    }

    [Fact]
    public void A_movement_carries_no_foreign_key_to_the_work_order_it_names()
    {
        // Work Orders is another module and another schema. The database cannot enforce this and
        // this module must never query that table (ARCHITECTURE.md's boundary rule).
        var workOrderId = EntityType<StockMovement>().FindProperty(nameof(StockMovement.WorkOrderId))!;

        Assert.Empty(workOrderId.GetContainingForeignKeys());
        Assert.True(workOrderId.IsNullable);
    }

    [Fact]
    public void A_level_and_a_movement_both_point_at_a_real_warehouse_row()
    {
        // A warehouse is reference data in this same schema, so unlike the work-order id this one is
        // a constraint the database keeps.
        Assert.Contains(
            EntityType<StockLevel>().GetForeignKeys(),
            key => key.GetConstraintName() == "fk_stock_levels_warehouse" && key.DeleteBehavior == DeleteBehavior.Restrict);

        Assert.Contains(
            EntityType<StockMovement>().GetForeignKeys(),
            key => key.GetConstraintName() == "fk_stock_movements_warehouse" && key.DeleteBehavior == DeleteBehavior.Restrict);
    }

    [Fact]
    public async Task Two_items_cannot_share_a_catalogue_code()
    {
        using var database = new InventoryTestDatabase();

        database.Context.StockItems.Add(Item("ITM-000001"));
        database.Context.StockItems.Add(Item("ITM-000001", "A second line under one code"));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_items_cannot_share_a_manufacturers_part_number()
    {
        // The service checks first; this is the index behind that check, which is what actually makes
        // holding one part twice impossible rather than merely unlikely.
        using var database = new InventoryTestDatabase();

        database.Context.StockItems.Add(Item("ITM-000001", partNumber: "TE-LV4-CONN"));
        database.Context.StockItems.Add(Item("ITM-000002", "The same part, catalogued twice", "TE-LV4-CONN"));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task Any_number_of_items_may_carry_no_part_number()
    {
        // Both Postgres and SQLite treat NULLs in a unique index as distinct, which is why the index
        // is unfiltered — no hand-written SQL predicate to drift out of step with a column name.
        using var database = new InventoryTestDatabase();

        database.Context.StockItems.Add(Item("ITM-000001"));
        database.Context.StockItems.Add(Item("ITM-000002", "Copper earth wire, 25 mm²"));

        await database.Context.SaveChangesAsync();

        await using var read = database.NewContext();

        Assert.Equal(2, await read.StockItems.CountAsync());
    }

    [Fact]
    public async Task A_level_and_its_ledger_are_saved_with_the_item_they_belong_to()
    {
        using var database = new InventoryTestDatabase();

        var item = Item("ITM-000001");
        var warehouse = database.Context.Warehouses.Single(candidate => candidate.Code == "LB").Id;

        item.Receive(warehouse, 2000m, new("system", "system"), DateTimeOffset.UnixEpoch, 4.85m, "DN-4471");

        database.Context.StockItems.Add(item);

        await database.Context.SaveChangesAsync();

        await using var read = database.NewContext();

        var stored = await read.StockItems
            .Include(candidate => candidate.Levels)
            .Include(candidate => candidate.Movements)
            .SingleAsync();

        // Through numeric-typed columns and back, exact. This is where a float column would return
        // 1999.9999999999998 for the quantity and 4.8500000000000005 for the cost.
        Assert.Equal(2000m, Assert.Single(stored.Levels).QuantityOnHand);
        Assert.Equal(4.85m, Assert.Single(stored.Movements).UnitCost);
    }

    [Fact]
    public void One_item_cannot_hold_two_levels_in_one_warehouse()
    {
        // Held to the model rather than provoked here, and proved against Postgres in the gate tier
        // (WP-1.2's split): the aggregate is the only thing that can open a level, so the race this
        // index exists for cannot be staged from inside the fast tier without reflection. Without
        // the index, two first deliveries racing would open two shelves for one pair and the item
        // would hold two half-counts.
        var index = Assert.Single(
            EntityType<StockLevel>().GetIndexes(),
            candidate => candidate.GetDatabaseName() == "ux_stock_levels_item_warehouse");

        Assert.True(index.IsUnique);

        Assert.Equal(
            [nameof(StockLevel.StockItemId), nameof(StockLevel.WarehouseId)],
            index.Properties.Select(property => property.Name).ToArray());
    }

    private static StockItem Item(string code, string name = "ACSR Raven 1/0 conductor", string? partNumber = null) =>
        StockItem.Register(code, StockItemCategory.Conductor, name, UnitOfMeasure.Metre, DateTimeOffset.UnixEpoch,
            manufacturerPartNumber: partNumber, unitCost: 4.85m);
}
