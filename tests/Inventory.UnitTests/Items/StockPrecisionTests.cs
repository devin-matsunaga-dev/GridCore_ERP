using GridCore.Modules.Inventory.Features.Items;
using GridCore.Modules.Inventory.Features.Shared;

namespace GridCore.Modules.Inventory.UnitTests.Items;

/// <summary>
/// What the store will and will not accept as a number. Pure guards, so they are proved here rather
/// than through an aggregate that only happens to call them.
/// </summary>
public class StockPrecisionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(0.5)]
    [InlineData(0.125)]
    public void A_quantity_the_column_can_hold_comes_back_unchanged(decimal quantity) =>
        Assert.Equal(quantity, StockQuantities.Require(quantity, UnitOfMeasure.Metre, "quantity"));

    [Fact]
    public void A_quantity_finer_than_three_places_is_refused() =>
        Assert.Throws<InventoryValidationException>(() =>
            StockQuantities.Require(0.0625m, UnitOfMeasure.Litre, "quantity"));

    [Fact]
    public void A_trailing_zero_is_not_a_finer_quantity() =>
        // 2.500 and 2.5 are the same number to three places. decimal keeps the scale it was written
        // with, so a rule implemented as "does the string have four digits after the point" would
        // refuse this — Round is what makes it a question about the value.
        Assert.Equal(2.500m, StockQuantities.Require(2.500m, UnitOfMeasure.Metre, "quantity"));

    [Fact]
    public void A_level_may_be_zero_but_a_movement_may_not()
    {
        Assert.Equal(0m, StockQuantities.RequireLevel(0m, UnitOfMeasure.Each, "minimumQuantity"));
        Assert.Throws<InventoryValidationException>(() => StockQuantities.RequireMovement(0m, UnitOfMeasure.Each, "quantity"));
    }

    [Fact]
    public void A_negative_level_is_refused() =>
        Assert.Throws<InventoryValidationException>(() => StockQuantities.RequireLevel(-1m, UnitOfMeasure.Each, "countedQuantity"));

    [Theory]
    [InlineData(UnitOfMeasure.Each, false)]
    [InlineData(UnitOfMeasure.Metre, true)]
    [InlineData(UnitOfMeasure.Kilogram, true)]
    [InlineData(UnitOfMeasure.Litre, true)]
    public void Only_counted_items_are_indivisible(UnitOfMeasure unit, bool divisible) =>
        Assert.Equal(divisible, UnitsOfMeasure.IsDivisible(unit));

    [Fact]
    public void A_cost_is_money_to_the_cent()
    {
        Assert.Equal(4.85m, StockCosts.Require(4.85m, "unitCost"));
        Assert.Equal(0m, StockCosts.Require(0m, "unitCost"));
        Assert.Throws<InventoryValidationException>(() => StockCosts.Require(4.8547m, "unitCost"));
    }

    [Fact]
    public void A_negative_cost_is_refused() =>
        // Money owed back on a wrong delivery is a credit note in Finance, not a stock line that
        // nets against every other one in a valuation.
        Assert.Throws<InventoryValidationException>(() => StockCosts.Require(-1m, "unitCost"));
}
