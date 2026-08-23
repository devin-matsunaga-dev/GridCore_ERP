namespace GridCore.Contracts.Events;

/// <summary>
/// Inventory received goods against a purchase order. Finance consumes this to post the payable
/// (debit inventory, credit AP).
/// </summary>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the goods were booked in.</param>
/// <param name="ReceiptId">The goods receipt in Inventory's schema.</param>
/// <param name="PurchaseOrderId">The purchase order received against.</param>
/// <param name="WarehouseId">Where the stock landed.</param>
/// <param name="VendorId">Who supplied it.</param>
/// <param name="TotalCost">Value of the receipt. Money is <see langword="decimal"/>, never a float.</param>
/// <param name="Currency">ISO 4217 code the amount is expressed in.</param>
/// <param name="Lines">What was received, line by line.</param>
public sealed record GoodsReceived(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ReceiptId,
    Guid PurchaseOrderId,
    Guid WarehouseId,
    Guid VendorId,
    decimal TotalCost,
    string Currency,
    IReadOnlyList<GoodsReceivedLine> Lines) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static GoodsReceived For(
        DateTimeOffset occurredAt,
        Guid receiptId,
        Guid purchaseOrderId,
        Guid warehouseId,
        Guid vendorId,
        string currency,
        IReadOnlyList<GoodsReceivedLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return new GoodsReceived(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            receiptId,
            purchaseOrderId,
            warehouseId,
            vendorId,
            lines.Sum(line => line.LineCost),
            currency,
            lines);
    }
}

/// <summary>One line of a goods receipt.</summary>
/// <param name="ItemId">The stocked item in Inventory's schema.</param>
/// <param name="ItemCode">Its catalogue code, so the ledger line reads without a lookup.</param>
/// <param name="Quantity">How many units were received.</param>
/// <param name="UnitCost">Cost of one unit. Money is <see langword="decimal"/>, never a float.</param>
public sealed record GoodsReceivedLine(Guid ItemId, string ItemCode, decimal Quantity, decimal UnitCost)
{
    /// <summary>What this line is worth: quantity times unit cost, in <see langword="decimal"/>.</summary>
    public decimal LineCost => Quantity * UnitCost;
}
