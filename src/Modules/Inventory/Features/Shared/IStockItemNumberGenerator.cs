using GridCore.Modules.Inventory.Data;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Inventory.Features.Shared;

/// <summary>
/// The prefix this module's registry numbers are issued under. The <i>shape</i> of a number is the
/// platform's (<see cref="RegistryNumbers"/>); what letters a catalogue code carries is the
/// Inventory module's own business.
/// </summary>
public static class InventoryNumbers
{
    /// <summary>
    /// Prefix of a catalogue code, e.g. <c>ITM-000001</c>. Three letters, like <c>AST-</c> and for
    /// the same reason: single letters are taken, and a storeman reading a code off a bin label
    /// should not be able to confuse it with a customer or an asset.
    /// </summary>
    public const string ItemCodePrefix = "ITM-";
}

/// <summary>
/// Issues the next catalogue code. A seam, so the numbering scheme is one registration away from
/// changing — a utility migrating from a legacy store system usually has to keep its own codes.
/// </summary>
public interface IStockItemNumberGenerator
{
    /// <summary>The next unused catalogue code.</summary>
    Task<string> NextItemCodeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Continues the code series from the highest code already issued, inside the caller's transaction.
/// </summary>
/// <remarks>
/// One <see cref="RegistryNumberSeries.NextAsync"/> over this module's own column; the race with a
/// concurrent registration and the ordering trade it depends on are documented there, because every
/// registry shares them.
/// </remarks>
public sealed class SequentialStockItemNumberGenerator(InventoryDbContext database) : IStockItemNumberGenerator
{
    /// <inheritdoc />
    public Task<string> NextItemCodeAsync(CancellationToken cancellationToken = default) =>
        RegistryNumberSeries.NextAsync(
            InventoryNumbers.ItemCodePrefix,
            database.StockItems
                .Where(item => item.ItemCode.StartsWith(InventoryNumbers.ItemCodePrefix))
                .OrderByDescending(item => item.ItemCode)
                .Select(item => item.ItemCode),
            cancellationToken);
}
