using GridCore.Modules.Inventory.Features.Shared;

namespace GridCore.Modules.Inventory.Features.Items;

/// <summary>
/// How exact a stock quantity is, and what happens to a caller who is more exact than that.
/// </summary>
/// <remarks>
/// Three decimal places, because a store cuts conductor to the millimetre and weighs fixings to the
/// gramme, and nothing a utility issues to a crew is finer than that. A quantity finer than the
/// column is <b>refused, not rounded</b> — the same call WP-1.1 made for a deposit finer than a cent
/// and WP-1.3 for a coordinate finer than six places: CONVENTIONS.md's central rounding helper still
/// has no home (WP-2.3 owns it), and <c>numeric(18,3)</c> would have truncated silently, leaving a
/// count nobody chose and a ledger that no longer adds up to it.
/// </remarks>
public static class StockQuantities
{
    /// <summary>Total digits a quantity is stored with.</summary>
    public const int Precision = 18;

    /// <summary>Digits after the point a quantity is stored with.</summary>
    public const int DecimalPlaces = 3;

    /// <summary>
    /// Checks <paramref name="quantity"/> is one the store can hold in <paramref name="unit"/> and
    /// returns it unchanged.
    /// </summary>
    /// <exception cref="InventoryValidationException">It is finer than the column, or a fraction of an indivisible unit.</exception>
    public static decimal Require(decimal quantity, UnitOfMeasure unit, string field)
    {
        if (decimal.Round(quantity, DecimalPlaces) != quantity)
        {
            throw new InventoryValidationException(
                $"'{field}' is finer than {DecimalPlaces} decimal places ({quantity}); the store cannot hold that quantity exactly.");
        }

        if (!UnitsOfMeasure.IsDivisible(unit) && decimal.Truncate(quantity) != quantity)
        {
            throw new InventoryValidationException(
                $"'{field}' is a fraction ({quantity}), and this item is counted by the {unit}. Only whole units can be held.");
        }

        return quantity;
    }

    /// <summary>
    /// Checks <paramref name="quantity"/> is a movement of something rather than of nothing, and
    /// returns it unchanged.
    /// </summary>
    /// <exception cref="InventoryValidationException">It is zero or negative, or not one the store can hold.</exception>
    public static decimal RequireMovement(decimal quantity, UnitOfMeasure unit, string field)
    {
        if (quantity <= 0)
        {
            // Direction is the movement's job, not the quantity's: a receipt of -5 is an issue
            // recorded under the wrong verb, and it would be invisible in a ledger filtered by type.
            throw new InventoryValidationException($"'{field}' must be more than zero; {quantity} is not a movement.");
        }

        return Require(quantity, unit, field);
    }

    /// <summary>
    /// Checks <paramref name="quantity"/> is a level the store can hold — zero included, since an
    /// empty shelf and a reorder level of nothing are both real.
    /// </summary>
    /// <exception cref="InventoryValidationException">It is negative, or not one the store can hold.</exception>
    public static decimal RequireLevel(decimal quantity, UnitOfMeasure unit, string field)
    {
        if (quantity < 0)
        {
            throw new InventoryValidationException($"'{field}' cannot be negative; {quantity} is.");
        }

        return Require(quantity, unit, field);
    }
}

/// <summary>
/// How exact a stock cost is. Money is <see langword="decimal"/> to the cent (invariant 4), and a
/// cost finer than that is refused rather than rounded, for the reason in <see cref="StockQuantities"/>.
/// </summary>
public static class StockCosts
{
    /// <summary>Total digits a cost is stored with.</summary>
    public const int Precision = 18;

    /// <summary>Digits after the point a cost is stored with — cents.</summary>
    public const int DecimalPlaces = 2;

    /// <summary>Checks <paramref name="cost"/> is an amount of money and returns it unchanged.</summary>
    /// <exception cref="InventoryValidationException">It is negative or finer than a cent.</exception>
    public static decimal Require(decimal cost, string field)
    {
        if (cost < 0)
        {
            // What a thing costs is not negative. Money owed back on a wrong delivery is a credit
            // note in Finance, not a stock line that nets against every other one in a valuation.
            throw new InventoryValidationException($"'{field}' cannot be negative; {cost} is.");
        }

        if (decimal.Round(cost, DecimalPlaces) != cost)
        {
            throw new InventoryValidationException(
                $"'{field}' is finer than a cent ({cost}); GridCore does not round money silently.");
        }

        return cost;
    }
}
