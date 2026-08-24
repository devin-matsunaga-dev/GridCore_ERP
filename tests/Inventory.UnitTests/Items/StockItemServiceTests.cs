using GridCore.Modules.Inventory.Features.Items;
using GridCore.Modules.Inventory.Features.Shared;
using GridCore.Modules.Inventory.Features.Warehouses;
using GridCore.Modules.Inventory.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Inventory.UnitTests.Items;

/// <summary>
/// The store end to end on SQLite in-memory: the movement, its level, its ledger line and its audit
/// entry, all in one transaction.
/// </summary>
public class StockItemServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static InventoryTestHost Host(out FakeClock clock)
    {
        clock = new FakeClock(Now);

        return new InventoryTestHost(clock, new FakeCurrentUser("subject-1", "Jesse Taisacan"));
    }

    [Fact]
    public async Task Registering_an_item_issues_the_next_code()
    {
        using var host = Host(out var clock);

        var first = await host.GivenItemAsync();

        clock.Advance(TimeSpan.FromSeconds(1));

        var second = await host.GivenItemAsync("Copper earth wire, 25 mm²");

        Assert.Equal("ITM-000001", first.ItemCode);
        Assert.Equal("ITM-000002", second.ItemCode);
    }

    [Fact]
    public async Task A_registration_stores_everything_the_caller_gave_it()
    {
        using var host = Host(out _);

        var registered = await host.WithStockAsync(stock => stock.RegisterAsync(new RegisterStockItemInput(
            StockItemCategory.Hardware,
            "LV connector kit, 4-way",
            UnitOfMeasure.Each,
            "Insulated piercing connectors, service drops",
            "TE-LV4-CONN",
            18.40m)));

        await using var read = host.NewInventoryContext();

        var stored = await read.StockItems.SingleAsync(item => item.Id == registered.Id);

        Assert.Equal("ITM-000001", stored.ItemCode);
        Assert.Equal("LV connector kit, 4-way", stored.Name);
        Assert.Equal(StockItemCategory.Hardware, stored.Category);
        Assert.Equal(UnitOfMeasure.Each, stored.Unit);
        Assert.Equal("TE-LV4-CONN", stored.ManufacturerPartNumber);
        Assert.Equal(18.40m, stored.UnitCost);
        Assert.True(stored.IsActive);
        Assert.Equal(Now, stored.RegisteredAt);
    }

    [Fact]
    public async Task A_receipt_writes_the_level_the_ledger_line_and_the_audit_entry_together()
    {
        using var host = Host(out _);

        var item = await host.GivenItemAsync();

        var movement = await host.WithStockAsync(stock => stock.ReceiveAsync(
            item.Id,
            new ReceiveStockInput(InventoryTestHost.LowerBase, 2000m, 4.85m, "DN-4471", "Delivered to the Lower Base store")));

        await using var read = host.NewInventoryContext();

        var level = await read.StockLevels.SingleAsync(candidate => candidate.StockItemId == item.Id);

        Assert.Equal(2000m, level.QuantityOnHand);
        Assert.Equal(InventoryTestHost.LowerBase, level.WarehouseId);
        Assert.Equal(Now, level.LastMovedAt);

        var line = await read.StockMovements.SingleAsync(candidate => candidate.Id == movement.Id);

        Assert.Equal(StockMovementType.Receipt, line.MovementType);
        Assert.Equal(2000m, line.QuantityChange);
        Assert.Equal(2000m, line.QuantityOnHandAfter);
        Assert.Equal(4.85m, line.UnitCost);
        Assert.Equal("DN-4471", line.Reference);

        // The actor is the caller, captured on the line rather than resolved later — a ledger read
        // years from now outlives directory entries (WP-1.2's rule, unchanged).
        Assert.Equal("subject-1", line.ActorId);
        Assert.Equal("Jesse Taisacan", line.ActorName);

        await using var platform = host.NewPlatformContext();

        var audit = await platform.AuditEntries.SingleAsync(entry => entry.Action == AuditActions.StockReceived);

        Assert.Equal(AuditEntityTypes.StockItem, audit.EntityType);
        Assert.Equal(item.Id.ToString(), audit.EntityId);
        Assert.Contains("\"quantityOnHand\":0", audit.BeforeJson, StringComparison.Ordinal);
        Assert.Contains("\"quantityOnHandAfter\":2000", audit.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_adjustment_audits_the_count_before_and_after()
    {
        using var host = Host(out _);

        var item = await host.GivenItemAsync("Transformer oil, mineral", UnitOfMeasure.Litre, StockItemCategory.Consumable, 3.15m);

        await host.WithStockAsync(stock => stock.ReceiveAsync(item.Id, new ReceiveStockInput(InventoryTestHost.LowerBase, 1000m)));

        await host.WithStockAsync(stock => stock.AdjustAsync(
            item.Id,
            new AdjustStockInput(InventoryTestHost.LowerBase, 940m, "Annual stock take: drum found short after a spill")));

        await using var platform = host.NewPlatformContext();

        var audit = await platform.AuditEntries.SingleAsync(entry => entry.Action == AuditActions.StockAdjusted);

        // The entry has to read as "was 1000, counted 940, because …" rather than merely "stock was
        // adjusted" — that is the difference between an audit trail and a log line.
        Assert.Contains("\"quantityOnHand\":1000", audit.BeforeJson, StringComparison.Ordinal);
        Assert.Contains("\"quantityChange\":-60", audit.AfterJson, StringComparison.Ordinal);
        Assert.Contains("drum found short", audit.AfterJson, StringComparison.Ordinal);
        Assert.Contains("LB", audit.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_issue_leaves_no_level_no_ledger_line_and_no_audit_entry()
    {
        // Failure path, in the shape that matters: the guard is inside the unit of work, so a
        // rejected movement must not leave a half-written trail behind it.
        using var host = Host(out _);

        var item = await host.GivenItemAsync();

        await host.WithStockAsync(stock => stock.ReceiveAsync(item.Id, new ReceiveStockInput(InventoryTestHost.LowerBase, 100m)));

        await Assert.ThrowsAsync<InventoryWorkflowException>(() => host.WithStockAsync(stock =>
            stock.IssueAsync(item.Id, new IssueStockInput(InventoryTestHost.LowerBase, 101m))));

        await using var read = host.NewInventoryContext();

        Assert.Equal(100m, (await read.StockLevels.SingleAsync(level => level.StockItemId == item.Id)).QuantityOnHand);
        Assert.Equal(1, await read.StockMovements.CountAsync(movement => movement.StockItemId == item.Id));

        await using var platform = host.NewPlatformContext();

        Assert.Equal(0, await platform.AuditEntries.CountAsync(entry => entry.Action == AuditActions.StockIssued));
    }

    [Fact]
    public async Task A_movement_naming_a_warehouse_that_does_not_exist_is_a_404()
    {
        using var host = Host(out _);

        var item = await host.GivenItemAsync();

        await Assert.ThrowsAsync<WarehouseNotFoundException>(() => host.WithStockAsync(stock =>
            stock.ReceiveAsync(item.Id, new ReceiveStockInput(Guid.CreateVersion7(Now), 10m))));
    }

    [Fact]
    public async Task A_movement_against_an_item_that_does_not_exist_is_a_404()
    {
        using var host = Host(out _);

        await Assert.ThrowsAsync<StockItemNotFoundException>(() => host.WithStockAsync(stock =>
            stock.ReceiveAsync(Guid.CreateVersion7(Now), new ReceiveStockInput(InventoryTestHost.LowerBase, 10m))));
    }

    [Fact]
    public async Task A_second_item_cannot_take_a_part_number_already_held()
    {
        using var host = Host(out _);

        await host.GivenItemAsync(partNumber: "TE-LV4-CONN");

        var refused = await Assert.ThrowsAsync<InventoryWorkflowException>(() =>
            host.GivenItemAsync("The same part, catalogued twice", partNumber: "TE-LV4-CONN"));

        Assert.Contains("ITM-000001", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Items_without_a_part_number_do_not_collide()
    {
        using var host = Host(out var clock);

        await host.GivenItemAsync();

        clock.Advance(TimeSpan.FromSeconds(1));

        var second = await host.GivenItemAsync("Copper earth wire, 25 mm²");

        Assert.Equal("ITM-000002", second.ItemCode);
    }

    [Fact]
    public async Task Correcting_a_line_may_keep_its_own_part_number()
    {
        using var host = Host(out _);

        var item = await host.GivenItemAsync(partNumber: "TE-LV4-CONN");

        var updated = await host.WithStockAsync(stock => stock.UpdateAsync(
            item.Id,
            new UpdateStockItemInput(
                StockItemCategory.Hardware,
                "LV connector kit, 4-way",
                UnitOfMeasure.Metre,
                19.00m,
                ManufacturerPartNumber: "TE-LV4-CONN")));

        Assert.Equal(19.00m, updated.UnitCost);
        Assert.Equal("TE-LV4-CONN", updated.ManufacturerPartNumber);
    }

    [Fact]
    public async Task Setting_a_reorder_level_is_audited_even_though_nothing_moved()
    {
        // Raising a reorder level is how a low-stock report is quietly silenced, which is precisely
        // the change somebody later wants to be able to look up.
        using var host = Host(out _);

        var item = await host.GivenItemAsync();

        var updated = await host.WithStockAsync(stock => stock.SetMinimumQuantityAsync(
            item.Id,
            new SetMinimumQuantityInput(InventoryTestHost.LowerBase, 500m)));

        Assert.True(updated.IsBelowMinimum);

        await using var read = host.NewInventoryContext();

        Assert.Equal(0, await read.StockMovements.CountAsync(movement => movement.StockItemId == item.Id));

        await using var platform = host.NewPlatformContext();

        var audit = await platform.AuditEntries.SingleAsync(entry => entry.Action == AuditActions.StockMinimumSet);

        Assert.Contains("\"minimumQuantity\":0", audit.BeforeJson, StringComparison.Ordinal);
        Assert.Contains("\"minimumQuantity\":500", audit.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ledger_reads_newest_first_and_narrows_by_warehouse_and_by_type()
    {
        using var host = Host(out var clock);

        var item = await host.GivenItemAsync();

        await host.WithStockAsync(stock => stock.ReceiveAsync(item.Id, new ReceiveStockInput(InventoryTestHost.LowerBase, 2000m)));

        clock.Advance(TimeSpan.FromSeconds(1));

        await host.WithStockAsync(stock => stock.ReceiveAsync(item.Id, new ReceiveStockInput(InventoryTestHost.Rota, 600m)));

        clock.Advance(TimeSpan.FromSeconds(1));

        await host.WithStockAsync(stock => stock.IssueAsync(item.Id, new IssueStockInput(InventoryTestHost.Rota, 180m)));

        var all = await host.WithStockAsync(stock => stock.MovementsAsync(item.Id));

        Assert.Equal(
            [StockMovementType.Issue, StockMovementType.Receipt, StockMovementType.Receipt],
            all.Select(movement => movement.MovementType).ToArray());

        var depot = await host.WithStockAsync(stock => stock.MovementsAsync(
            item.Id,
            new StockMovementQuery(InventoryTestHost.Rota)));

        Assert.Equal(2, depot.Count);

        var issues = await host.WithStockAsync(stock => stock.MovementsAsync(
            item.Id,
            new StockMovementQuery(MovementType: StockMovementType.Issue)));

        Assert.Equal(-180m, Assert.Single(issues).QuantityChange);
    }

    [Fact]
    public async Task The_ledger_of_an_item_that_does_not_exist_is_a_404() =>
        // Distinguished from an item that has simply never moved, which is a real state: an empty
        // list for a missing id would say the item existed.
        await Assert.ThrowsAsync<StockItemNotFoundException>(async () =>
        {
            using var host = Host(out _);

            await host.WithStockAsync(stock => stock.MovementsAsync(Guid.CreateVersion7(Now)));
        });

    [Fact]
    public async Task The_catalogue_list_carries_the_levels_and_hides_discontinued_lines_by_default()
    {
        using var host = Host(out var clock);

        var carried = await host.GivenItemAsync();

        clock.Advance(TimeSpan.FromSeconds(1));

        var dropped = await host.GivenItemAsync("Wooden crossarm, 2.4 m", UnitOfMeasure.Each, StockItemCategory.Hardware, 88.00m);

        await host.WithStockAsync(stock => stock.ReceiveAsync(carried.Id, new ReceiveStockInput(InventoryTestHost.LowerBase, 2000m)));

        await host.WithStockAsync(stock => stock.UpdateAsync(
            dropped.Id,
            new UpdateStockItemInput(
                StockItemCategory.Hardware,
                "Wooden crossarm, 2.4 m",
                UnitOfMeasure.Each,
                88.00m,
                IsActive: false,
                StatusReason: "Superseded by the composite crossarm")));

        var active = await host.WithStockAsync(stock => stock.ListAsync(new StockItemQuery()));

        Assert.Equal(carried.Id, Assert.Single(active).Id);
        Assert.Equal(2000m, Assert.Single(active).TotalOnHand);

        var everything = await host.WithStockAsync(stock => stock.ListAsync(new StockItemQuery(IncludeInactive: true)));

        Assert.Equal(2, everything.Count);
    }

    [Fact]
    public async Task The_catalogue_list_filters_by_search_category_and_warehouse()
    {
        using var host = Host(out var clock);

        var conductor = await host.GivenItemAsync(partNumber: "ACSR-RAVEN-1/0");

        clock.Advance(TimeSpan.FromSeconds(1));

        var gloves = await host.GivenItemAsync("Insulated gloves, class 2", UnitOfMeasure.Each, StockItemCategory.Safety, 145.00m);

        await host.WithStockAsync(stock => stock.ReceiveAsync(conductor.Id, new ReceiveStockInput(InventoryTestHost.LowerBase, 2000m)));
        await host.WithStockAsync(stock => stock.ReceiveAsync(gloves.Id, new ReceiveStockInput(InventoryTestHost.Rota, 12m)));

        var byName = await host.WithStockAsync(stock => stock.ListAsync(new StockItemQuery(Search: "gloves")));

        Assert.Equal(gloves.Id, Assert.Single(byName).Id);

        var byPartNumber = await host.WithStockAsync(stock => stock.ListAsync(new StockItemQuery(Search: "raven")));

        Assert.Equal(conductor.Id, Assert.Single(byPartNumber).Id);

        var byCode = await host.WithStockAsync(stock => stock.ListAsync(new StockItemQuery(Search: "itm-000002")));

        Assert.Equal(gloves.Id, Assert.Single(byCode).Id);

        var byCategory = await host.WithStockAsync(stock => stock.ListAsync(new StockItemQuery(Category: StockItemCategory.Safety)));

        Assert.Equal(gloves.Id, Assert.Single(byCategory).Id);

        var byWarehouse = await host.WithStockAsync(stock => stock.ListAsync(
            new StockItemQuery(WarehouseId: InventoryTestHost.Rota)));

        Assert.Equal(gloves.Id, Assert.Single(byWarehouse).Id);
    }

    [Fact]
    public async Task The_low_stock_filter_answers_in_sql_and_narrows_to_one_warehouse()
    {
        // The low-stock report, which is the query the whole flag exists for — and the composition
        // that makes "low in the north depot" mean low *there*.
        using var host = Host(out var clock);

        var connectors = await host.GivenItemAsync("LV connector kit, 4-way", UnitOfMeasure.Each, StockItemCategory.Hardware, 18.40m);

        clock.Advance(TimeSpan.FromSeconds(1));

        var conductor = await host.GivenItemAsync();

        await host.WithStockAsync(stock => stock.SetMinimumQuantityAsync(
            connectors.Id, new SetMinimumQuantityInput(InventoryTestHost.Rota, 20m)));

        await host.WithStockAsync(stock => stock.SetMinimumQuantityAsync(
            conductor.Id, new SetMinimumQuantityInput(InventoryTestHost.LowerBase, 500m)));

        await host.WithStockAsync(stock => stock.ReceiveAsync(connectors.Id, new ReceiveStockInput(InventoryTestHost.Rota, 40m)));
        await host.WithStockAsync(stock => stock.ReceiveAsync(conductor.Id, new ReceiveStockInput(InventoryTestHost.LowerBase, 2000m)));

        Assert.Empty(await host.WithStockAsync(stock => stock.ListAsync(new StockItemQuery(BelowMinimum: true))));

        await host.WithStockAsync(stock => stock.IssueAsync(connectors.Id, new IssueStockInput(InventoryTestHost.Rota, 26m)));

        var low = await host.WithStockAsync(stock => stock.ListAsync(new StockItemQuery(BelowMinimum: true)));

        Assert.Equal(connectors.Id, Assert.Single(low).Id);

        // Low on Rota, not at Lower Base — where this item is not stocked at all.
        Assert.Single(await host.WithStockAsync(stock => stock.ListAsync(
            new StockItemQuery(WarehouseId: InventoryTestHost.Rota, BelowMinimum: true))));

        Assert.Empty(await host.WithStockAsync(stock => stock.ListAsync(
            new StockItemQuery(WarehouseId: InventoryTestHost.LowerBase, BelowMinimum: true))));
    }

    [Fact]
    public async Task An_item_read_back_carries_its_levels_and_its_ledger()
    {
        using var host = Host(out _);

        var item = await host.GivenItemAsync();

        await host.WithStockAsync(stock => stock.ReceiveAsync(item.Id, new ReceiveStockInput(InventoryTestHost.LowerBase, 2000m)));
        await host.WithStockAsync(stock => stock.IssueAsync(item.Id, new IssueStockInput(InventoryTestHost.LowerBase, 180m)));

        var read = await host.WithStockAsync(stock => stock.FindAsync(item.Id));

        Assert.NotNull(read);
        Assert.Equal(1820m, read.TotalOnHand);
        Assert.Equal(2, read.Movements.Count);
        Assert.Equal(1820m, Assert.Single(read.Levels).QuantityOnHand);
    }

    [Fact]
    public async Task The_warehouse_list_says_what_each_one_holds_and_what_is_low()
    {
        using var host = Host(out _);

        var item = await host.GivenItemAsync("LV connector kit, 4-way", UnitOfMeasure.Each, StockItemCategory.Hardware, 18.40m);

        await host.WithStockAsync(stock => stock.SetMinimumQuantityAsync(
            item.Id, new SetMinimumQuantityInput(InventoryTestHost.Rota, 20m)));

        await host.WithStockAsync(stock => stock.ReceiveAsync(item.Id, new ReceiveStockInput(InventoryTestHost.Rota, 14m)));
        await host.WithStockAsync(stock => stock.ReceiveAsync(item.Id, new ReceiveStockInput(InventoryTestHost.LowerBase, 120m)));

        var warehouses = await host.InScopeAsync(services =>
            services.GetRequiredService<IWarehouseService>().ListAsync());

        Assert.Equal(["LB", "ROTA", "TINIAN"], warehouses.Select(warehouse => warehouse.Code).ToArray());

        var rota = warehouses.Single(warehouse => warehouse.Code == "ROTA");

        Assert.Equal(1, rota.LinesHeld);
        Assert.Equal(1, rota.LinesBelowMinimum);

        var lowerBase = warehouses.Single(warehouse => warehouse.Code == "LB");

        Assert.Equal(1, lowerBase.LinesHeld);
        Assert.Equal(0, lowerBase.LinesBelowMinimum);

        var tinian = warehouses.Single(warehouse => warehouse.Code == "TINIAN");

        Assert.Equal(0, tinian.LinesHeld);
    }
}
