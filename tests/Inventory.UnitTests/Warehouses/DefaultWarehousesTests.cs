using GridCore.Modules.Inventory.Features.Warehouses;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Inventory.UnitTests.Warehouses;

/// <summary>
/// The warehouses GridCore ships. Reference data, not demo data: stock cannot be received or issued
/// without somewhere to be, so a migrated database already has these (ARCHITECTURE.md invariant 8).
/// </summary>
public class DefaultWarehousesTests
{
    [Fact]
    public void Codes_and_ids_are_unique()
    {
        Assert.Equal(
            DefaultWarehouses.All.Count,
            DefaultWarehouses.All.Select(warehouse => warehouse.Code).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(
            DefaultWarehouses.All.Count,
            DefaultWarehouses.All.Select(warehouse => warehouse.Id).Distinct().Count());
    }

    [Fact]
    public void Every_shipped_warehouse_is_active_and_locatable()
    {
        Assert.All(DefaultWarehouses.All, warehouse =>
        {
            Assert.True(warehouse.IsActive);
            Assert.False(string.IsNullOrWhiteSpace(warehouse.Location));
        });
    }

    [Fact]
    public void A_warehouse_id_is_the_same_every_time_the_set_is_built()
    {
        var rebuilt = Warehouse.Reference(DefaultWarehouses.MainStore, "Main store", "1 Utility Way, Central depot");

        Assert.Equal(rebuilt.Id, DefaultWarehouses.Require(DefaultWarehouses.MainStore).Id);
    }

    [Fact]
    public void Asking_for_a_warehouse_that_does_not_exist_throws()
    {
        Assert.Throws<KeyNotFoundException>(() => DefaultWarehouses.Require("NOWHERE"));
    }

    [Fact]
    public void A_lower_case_code_is_refused()
    {
        // Failure path: codes are quoted by people and matched by machines. Without one canonical
        // case, "main" and "MAIN" become two warehouses holding half the stock each.
        var refused = Assert.Throws<ArgumentException>(() => Warehouse.Reference("main", "Main store", null));

        Assert.Contains("upper case", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_over_long_location_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Warehouse.Reference(
            "TEST", "Test", new string('x', Warehouse.LocationLength + 1)));
    }

    [Fact]
    public async Task Creating_the_schema_seeds_every_warehouse()
    {
        using var database = new InventoryTestDatabase();

        await using var context = database.NewContext();

        var seeded = await context.Warehouses.OrderBy(warehouse => warehouse.Code).ToListAsync();

        Assert.Equal(
            DefaultWarehouses.All.Select(warehouse => warehouse.Code).Order(StringComparer.Ordinal),
            seeded.Select(warehouse => warehouse.Code));
    }

    [Fact]
    public async Task Two_warehouses_cannot_share_a_code()
    {
        using var database = new InventoryTestDatabase();

        database.Context.Warehouses.Add(Warehouse.Reference(DefaultWarehouses.MainStore, "A second main store", null));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }
}
