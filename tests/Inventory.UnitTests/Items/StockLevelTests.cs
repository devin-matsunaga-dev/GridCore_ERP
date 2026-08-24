using GridCore.Modules.Inventory.Features.Items;

namespace GridCore.Modules.Inventory.UnitTests.Items;

/// <summary>
/// The low-stock rule. It exists twice — as a property the aggregate reads and as an expression the
/// database answers — so these tests are what hold the two together, the same way WP-1.2's tests
/// hold a hand-written index filter to the model it filters.
/// </summary>
public class StockLevelTests
{
    private static readonly Func<StockLevel, bool> Translated = StockLevel.BelowMinimum.Compile();

    public static TheoryData<decimal, decimal, bool> Cases => new()
    {
        // On hand, reorder level, expected.
        { 40m, 20m, false },
        { 21m, 20m, false },

        // At the level counts as low: a reorder level is the point you reorder at, not the point you
        // have already gone past.
        { 20m, 20m, true },
        { 14m, 20m, true },
        { 0m, 20m, true },

        // Nobody has set a reorder level, so an empty shelf is a line the store does not carry
        // rather than a line to chase. Flagging every one of them is how a low-stock report becomes
        // something nobody reads.
        { 0m, 0m, false },
        { 500m, 0m, false },
        { 0.5m, 0.25m, false },
        { 0.25m, 0.25m, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_rule_answers_the_same_whether_it_is_asked_in_memory_or_in_sql(
        decimal quantityOnHand,
        decimal minimumQuantity,
        bool expected)
    {
        var level = LevelOf(quantityOnHand, minimumQuantity);

        Assert.Equal(expected, StockLevel.IsBelow(quantityOnHand, minimumQuantity));
        Assert.Equal(expected, level.IsBelowMinimum);

        // Editing one copy of the rule without the other gives a screen that lists everything as
        // low, or nothing. This is that mistake, caught in the fast loop.
        Assert.Equal(expected, Translated(level));
    }

    private static StockLevel LevelOf(decimal quantityOnHand, decimal minimumQuantity)
    {
        var item = StockItem.Register("ITM-000001", StockItemCategory.Hardware, "Something", UnitOfMeasure.Metre, DateTimeOffset.UnixEpoch);
        var warehouse = Guid.CreateVersion7(DateTimeOffset.UnixEpoch);

        item.SetMinimumQuantity(warehouse, minimumQuantity, DateTimeOffset.UnixEpoch);

        if (quantityOnHand > 0)
        {
            item.Receive(warehouse, quantityOnHand, new("system", "system"), DateTimeOffset.UnixEpoch);
        }

        return item.LevelIn(warehouse)!;
    }
}
