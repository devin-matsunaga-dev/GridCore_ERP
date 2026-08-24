using GridCore.Modules.Inventory.Features.Items;
using GridCore.Modules.Inventory.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Inventory.UnitTests.Items;

/// <summary>
/// The store's arithmetic and its guards, with no database anywhere near it. This is where the
/// adjustment math WP-1.4 owes actually lives, so this is where it is proved.
/// </summary>
public class StockItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly RegistryActor Storeman = new("demo:warehouse", "Wes Store (demo)");
    private static readonly Guid Main = Guid.CreateVersion7(Now);
    private static readonly Guid Depot = Guid.CreateVersion7(Now.AddSeconds(1));

    private static StockItem AnItem(UnitOfMeasure unit = UnitOfMeasure.Metre, decimal unitCost = 4.85m) =>
        StockItem.Register("ITM-000001", StockItemCategory.Conductor, "ACSR Raven 1/0", unit, Now, unitCost: unitCost);

    [Fact]
    public void A_registered_item_is_held_nowhere_and_has_no_ledger()
    {
        var item = AnItem();

        // A catalogue line is not stock arriving. Three levels of zero would say the store had
        // looked at three shelves and found nothing, which is a different fact from never having
        // carried the line there at all.
        Assert.Empty(item.Levels);
        Assert.Empty(item.Movements);
        Assert.Equal(0m, item.TotalOnHand);
        Assert.False(item.IsBelowMinimum);
        Assert.True(item.IsActive);
    }

    [Fact]
    public void Receiving_stock_opens_a_level_and_writes_the_ledger_line()
    {
        var item = AnItem();

        var movement = item.Receive(Main, 2000m, Storeman, Now, unitCost: 4.85m, reference: "DN-4471");

        Assert.Equal(2000m, item.OnHandIn(Main));
        Assert.Equal(2000m, item.TotalOnHand);

        Assert.Equal(StockMovementType.Receipt, movement.MovementType);
        Assert.Equal(2000m, movement.QuantityChange);
        Assert.Equal(2000m, movement.QuantityOnHandAfter);
        Assert.Equal(9700m, movement.Value);
        Assert.Equal("DN-4471", movement.Reference);
        Assert.Equal("demo:warehouse", movement.ActorId);
        Assert.Equal("Wes Store (demo)", movement.ActorName);
    }

    [Fact]
    public void Issuing_stock_takes_it_off_the_shelf_and_records_the_job()
    {
        var item = AnItem();
        var job = Guid.CreateVersion7(Now);

        item.Receive(Main, 500m, Storeman, Now);

        var movement = item.Issue(Main, 180m, Storeman, Now, workOrderId: job);

        Assert.Equal(320m, item.OnHandIn(Main));

        // Signed, so summing the ledger reproduces the quantity on hand with no lookup table of
        // which movement types count as which direction.
        Assert.Equal(-180m, movement.QuantityChange);
        Assert.Equal(320m, movement.QuantityOnHandAfter);
        Assert.Equal(job, movement.WorkOrderId);
        Assert.Equal(320m, item.Movements.Sum(line => line.QuantityChange));
    }

    [Fact]
    public void Stock_is_held_per_warehouse_and_a_movement_touches_only_one_shelf()
    {
        var item = AnItem();

        item.Receive(Main, 2000m, Storeman, Now);
        item.Receive(Depot, 600m, Storeman, Now);
        item.Issue(Depot, 180m, Storeman, Now);

        Assert.Equal(2000m, item.OnHandIn(Main));
        Assert.Equal(420m, item.OnHandIn(Depot));
        Assert.Equal(2420m, item.TotalOnHand);
    }

    [Fact]
    public void Issuing_more_than_is_on_hand_is_refused_and_changes_nothing()
    {
        // Failure path. A store that can go below zero is a store whose count means nothing.
        var item = AnItem();

        item.Receive(Main, 100m, Storeman, Now);

        var refused = Assert.Throws<InventoryWorkflowException>(() => item.Issue(Main, 101m, Storeman, Now));

        Assert.Contains("100", refused.Message, StringComparison.Ordinal);
        Assert.Equal(100m, item.OnHandIn(Main));
        Assert.Single(item.Movements);
    }

    [Fact]
    public void Issuing_from_a_warehouse_that_holds_none_is_refused()
    {
        var item = AnItem();

        item.Receive(Main, 100m, Storeman, Now);

        Assert.Throws<InventoryWorkflowException>(() => item.Issue(Depot, 1m, Storeman, Now));

        // No shelf was opened on the way to being refused.
        Assert.DoesNotContain(item.Levels, level => level.WarehouseId == Depot);
    }

    [Theory]
    [InlineData(40, 32, -8)]
    [InlineData(40, 47, 7)]
    [InlineData(0, 6, 6)]
    public void An_adjustment_derives_the_difference_from_what_was_counted(int onHand, int counted, int expectedChange)
    {
        // The adjustment math WP-1.4 asks for. The caller states the count; the store does the
        // subtraction — which is what stops "we are eight short" from being entered as eight and
        // going the wrong way.
        var item = AnItem(UnitOfMeasure.Each);

        if (onHand > 0)
        {
            item.Receive(Main, onHand, Storeman, Now);
        }

        var movement = item.Adjust(Main, counted, "Annual stock take", Storeman, Now);

        Assert.Equal(expectedChange, movement.QuantityChange);
        Assert.Equal(counted, movement.QuantityOnHandAfter);
        Assert.Equal(counted, item.OnHandIn(Main));
        Assert.Equal(StockMovementType.Adjustment, movement.MovementType);
    }

    [Fact]
    public void An_adjustment_that_agrees_with_the_system_is_refused()
    {
        // Failure path, and the deliberate opposite of Asset.AssessCondition recording "inspected,
        // still Fair": there the assessment is the record, here the ledger is for movements, and a
        // zero line in a ledger read to explain a quantity is noise.
        var item = AnItem(UnitOfMeasure.Each);

        item.Receive(Main, 40m, Storeman, Now);

        var refused = Assert.Throws<InventoryWorkflowException>(() => item.Adjust(Main, 40m, "Counted, all present", Storeman, Now));

        Assert.Contains("nothing to correct", refused.Message, StringComparison.Ordinal);
        Assert.Single(item.Movements);
    }

    [Fact]
    public void An_adjustment_with_no_reason_is_refused()
    {
        // Invariant 5: an adjustment moves stock with nothing physically moving, so an unexplained
        // one is exactly the write an auditor comes looking for.
        var item = AnItem(UnitOfMeasure.Each);

        item.Receive(Main, 40m, Storeman, Now);

        Assert.Throws<InventoryValidationException>(() => item.Adjust(Main, 32m, "   ", Storeman, Now));
        Assert.Equal(40m, item.OnHandIn(Main));
    }

    [Fact]
    public void An_adjustment_can_find_stock_where_the_system_had_none()
    {
        var item = AnItem(UnitOfMeasure.Each);

        var movement = item.Adjust(Depot, 6m, "Found on the wrong shelf during the stock take", Storeman, Now);

        Assert.Equal(6m, movement.QuantityChange);
        Assert.Equal(6m, item.OnHandIn(Depot));
    }

    [Fact]
    public void A_negative_count_is_refused()
    {
        var item = AnItem(UnitOfMeasure.Each);

        Assert.Throws<InventoryValidationException>(() => item.Adjust(Main, -1m, "Typed in wrong", Storeman, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_movement_of_nothing_or_of_a_negative_quantity_is_refused(int quantity)
    {
        // Direction is the movement's job, not the quantity's: a receipt of -5 is an issue recorded
        // under the wrong verb, and it would be invisible in a ledger filtered by type.
        var item = AnItem();

        Assert.Throws<InventoryValidationException>(() => item.Receive(Main, quantity, Storeman, Now));
        Assert.Throws<InventoryValidationException>(() => item.Issue(Main, quantity, Storeman, Now));
    }

    [Fact]
    public void A_fraction_of_a_counted_item_is_refused()
    {
        // Half a metre of conductor is a real quantity; half a connector is a broken connector.
        var counted = AnItem(UnitOfMeasure.Each);
        var measured = AnItem(UnitOfMeasure.Metre);

        Assert.Throws<InventoryValidationException>(() => counted.Receive(Main, 2.5m, Storeman, Now));

        measured.Receive(Main, 2.5m, Storeman, Now);

        Assert.Equal(2.5m, measured.OnHandIn(Main));
    }

    [Fact]
    public void A_quantity_finer_than_the_store_can_hold_is_refused_rather_than_rounded()
    {
        // Same call as WP-1.1's deposit finer than a cent and WP-1.3's coordinate finer than six
        // places: numeric(18,3) would have truncated silently, leaving a count nobody chose and a
        // ledger that no longer adds up to it.
        var item = AnItem();

        var refused = Assert.Throws<InventoryValidationException>(() => item.Receive(Main, 1.00005m, Storeman, Now));

        Assert.Contains("decimal places", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cost_finer_than_a_cent_is_refused_rather_than_rounded()
    {
        var item = AnItem();

        Assert.Throws<InventoryValidationException>(() => item.Receive(Main, 10m, Storeman, Now, unitCost: 4.8547m));
        Assert.Throws<InventoryValidationException>(() => AnItem(unitCost: 4.8547m));
    }

    [Fact]
    public void A_reorder_level_opens_a_shelf_that_holds_nothing_yet()
    {
        // The state a store spends most of its time in: on order, nothing delivered, and showing on
        // the low-stock report so somebody chases it.
        var item = AnItem();

        item.SetMinimumQuantity(Main, 500m, Now);

        Assert.Equal(0m, item.OnHandIn(Main));
        Assert.True(item.IsBelowMinimum);

        // Not a movement: nothing came in and nothing went out.
        Assert.Empty(item.Movements);
    }

    [Fact]
    public void A_shelf_falls_below_its_reorder_level_as_stock_goes_out()
    {
        var item = AnItem(UnitOfMeasure.Each);

        item.SetMinimumQuantity(Depot, 20m, Now);
        item.Receive(Depot, 40m, Storeman, Now);

        Assert.False(item.IsBelowMinimum);

        item.Issue(Depot, 26m, Storeman, Now);

        Assert.True(item.IsBelowMinimum);
        Assert.True(item.LevelIn(Depot)!.IsBelowMinimum);
    }

    [Fact]
    public void A_discontinued_line_refuses_deliveries_but_still_lets_the_shelf_be_cleared()
    {
        var item = AnItem(UnitOfMeasure.Each);

        item.Receive(Main, 40m, Storeman, Now);
        item.UpdateDetails(
            item.Category,
            item.Name,
            item.Unit,
            item.UnitCost,
            isActive: false,
            statusReason: "Superseded by the composite crossarm");

        Assert.Throws<InventoryWorkflowException>(() => item.Receive(Main, 10m, Storeman, Now));

        // Issuing is still allowed, deliberately: the stock on the shelf has to go somewhere, and
        // the crew using up the last of it is how that happens.
        item.Issue(Main, 38m, Storeman, Now);

        Assert.Equal(2m, item.OnHandIn(Main));
        Assert.Equal("Superseded by the composite crossarm", item.StatusReason);
    }

    [Fact]
    public void Correcting_details_without_moving_the_flag_leaves_the_reason_alone()
    {
        // WP-1.1's rule for a premise, unchanged: an address typo fix cannot erase why it is out of
        // service, and a description fix cannot erase why a line was discontinued.
        var item = AnItem();

        item.UpdateDetails(item.Category, item.Name, item.Unit, item.UnitCost, isActive: false, statusReason: "Superseded");
        item.UpdateDetails(item.Category, "ACSR Raven 1/0 conductor", item.Unit, item.UnitCost, isActive: false, statusReason: "typo fix");

        Assert.Equal("Superseded", item.StatusReason);
        Assert.Equal("ACSR Raven 1/0 conductor", item.Name);
    }

    [Fact]
    public void The_unit_of_measure_is_fixed_once_stock_has_moved()
    {
        // Failure path. Re-denominating 240 metres as 240 each would not convert the ledger behind
        // it — it would silently reinterpret every line.
        var item = AnItem();

        item.Receive(Main, 240m, Storeman, Now);

        var refused = Assert.Throws<InventoryWorkflowException>(() =>
            item.UpdateDetails(item.Category, item.Name, UnitOfMeasure.Each, item.UnitCost, isActive: true));

        Assert.Contains("unit of measure", refused.Message, StringComparison.Ordinal);
        Assert.Equal(UnitOfMeasure.Metre, item.Unit);
    }

    [Fact]
    public void The_unit_of_measure_can_still_be_corrected_before_anything_moves()
    {
        var item = AnItem();

        item.UpdateDetails(item.Category, item.Name, UnitOfMeasure.Kilogram, item.UnitCost, isActive: true);

        Assert.Equal(UnitOfMeasure.Kilogram, item.Unit);
    }

    [Fact]
    public void An_undeclared_category_or_unit_is_refused()
    {
        Assert.Throws<InventoryValidationException>(() => StockItem.Register(
            "ITM-000001", (StockItemCategory)99, "Something", UnitOfMeasure.Each, Now));

        Assert.Throws<InventoryValidationException>(() => StockItem.Register(
            "ITM-000001", StockItemCategory.Hardware, "Something", (UnitOfMeasure)99, Now));
    }

    [Fact]
    public void An_item_needs_a_code_and_a_name()
    {
        Assert.Throws<InventoryValidationException>(() => StockItem.Register(
            " ", StockItemCategory.Hardware, "Something", UnitOfMeasure.Each, Now));

        Assert.Throws<InventoryValidationException>(() => StockItem.Register(
            "ITM-000001", StockItemCategory.Hardware, "  ", UnitOfMeasure.Each, Now));
    }

    [Fact]
    public void Free_text_is_cleaned_the_way_every_registry_cleans_it()
    {
        var item = StockItem.Register(
            "ITM-000001",
            StockItemCategory.Hardware,
            "  LV connector kit  ",
            UnitOfMeasure.Each,
            Now,
            description: "   ",
            manufacturerPartNumber: " TE-LV4-CONN ");

        Assert.Equal("LV connector kit", item.Name);
        Assert.Null(item.Description);
        Assert.Equal("TE-LV4-CONN", item.ManufacturerPartNumber);
    }
}
