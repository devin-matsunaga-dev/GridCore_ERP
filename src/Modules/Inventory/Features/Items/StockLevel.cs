using System.Linq.Expressions;

namespace GridCore.Modules.Inventory.Features.Items;

/// <summary>
/// How much of one item is held in one warehouse, and how little it is allowed to fall to. The row
/// a storeman actually reads: an item exists once in the catalogue, but stock is somewhere.
/// </summary>
/// <remarks>
/// Created by <see cref="StockItem"/> the first time the item moves in a warehouse, so a level that
/// exists is a level something has happened to — an item stocked nowhere has no rows rather than
/// three rows of zero.
/// </remarks>
public sealed class StockLevel
{
    private StockLevel()
    {
        // EF materialisation.
    }

    /// <summary>Identifier of this level. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The catalogue item this is a level of.</summary>
    public Guid StockItemId { get; private init; }

    /// <summary>The warehouse it is held in. A row of <c>inventory.warehouses</c> — same schema, real foreign key.</summary>
    public Guid WarehouseId { get; private init; }

    /// <summary>How much is on the shelf.</summary>
    public decimal QuantityOnHand { get; private set; }

    /// <summary>
    /// How low it may fall before somebody should be reordering. Held per warehouse rather than per
    /// item: the main store and a crew depot do not want the same reorder level for the same
    /// connector. Zero means nobody has set one.
    /// </summary>
    public decimal MinimumQuantity { get; private set; }

    /// <summary>When stock last moved here.</summary>
    public DateTimeOffset? LastMovedAt { get; private set; }

    /// <summary>
    /// Whether this shelf is at or below its reorder level — the low-stock flag WP-1.4 owes.
    /// Computed, never stored: a stored flag goes stale the moment a movement forgets to refresh it.
    /// </summary>
    public bool IsBelowMinimum => IsBelow(QuantityOnHand, MinimumQuantity);

    /// <summary>
    /// The same rule as a predicate the database can answer, for the <c>?belowMinimum=</c> filter.
    /// </summary>
    /// <remarks>
    /// EF cannot translate a call to <see cref="IsBelow"/>, so the rule is written twice — and a
    /// fast test runs a table of cases through both to prove they agree, exactly as WP-1.2 holds its
    /// hand-written index filter to the model. Editing one without the other is the failure this
    /// guards: a screen that lists everything as low, or nothing.
    /// </remarks>
    public static Expression<Func<StockLevel, bool>> BelowMinimum { get; } =
        level => level.MinimumQuantity > 0 && level.QuantityOnHand <= level.MinimumQuantity;

    /// <summary>
    /// Whether <paramref name="quantityOnHand"/> is at or below <paramref name="minimumQuantity"/>.
    /// </summary>
    /// <remarks>
    /// At <i>or below</i>, because a reorder level is the point you reorder at, not the point you
    /// have already gone past. A minimum of zero is nobody having set one, so an empty shelf with no
    /// reorder level is not "low stock" — it is a line the store does not carry, and flagging every
    /// one of them is how a low-stock report becomes something nobody reads.
    /// </remarks>
    public static bool IsBelow(decimal quantityOnHand, decimal minimumQuantity) =>
        minimumQuantity > 0 && quantityOnHand <= minimumQuantity;

    /// <summary>Opens a level for an item in a warehouse, holding nothing.</summary>
    internal static StockLevel For(Guid stockItemId, Guid warehouseId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            StockItemId = stockItemId,
            WarehouseId = warehouseId,
            QuantityOnHand = 0m,
            MinimumQuantity = 0m,
        };

    /// <summary>
    /// Moves the quantity on hand by <paramref name="change"/> and returns what is left.
    /// </summary>
    /// <remarks>
    /// Deliberately does no checking. Whether there is enough to issue, and whether the item may be
    /// received at all, are the aggregate's rules — <see cref="StockItem"/> is the only thing that
    /// can reach this, and a guard here as well would be a second copy of a rule to keep in step.
    /// </remarks>
    internal decimal Apply(decimal change, DateTimeOffset now)
    {
        QuantityOnHand += change;
        LastMovedAt = now;

        return QuantityOnHand;
    }

    /// <summary>Sets the reorder level.</summary>
    internal void SetMinimum(decimal minimumQuantity) => MinimumQuantity = minimumQuantity;
}
