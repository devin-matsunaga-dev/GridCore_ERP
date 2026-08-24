using GridCore.Modules.Inventory.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Inventory.Features.Items;

/// <summary>Why a quantity on hand changed.</summary>
public enum StockMovementType
{
    /// <summary>Stock came in. WP-4.1's goods receipt is this movement with a purchase order behind it.</summary>
    Receipt = 1,

    /// <summary>Stock went out to a job. WP-3.3's parts issuance is this movement with a work order behind it.</summary>
    Issue = 2,

    /// <summary>
    /// A count correction — the shelf and the system disagreed and the shelf won. The sensitive one:
    /// permission-gated on <c>inventory.adjust</c> and audited (invariant 5), because it is the only
    /// movement that changes stock without anything physically going anywhere.
    /// </summary>
    Adjustment = 3,
}

/// <summary>
/// One line of the stock ledger: what moved, where, which way, and what was left afterwards.
/// Append-only — a movement is a record of what happened, so a mistake is corrected by the next line
/// rather than by editing the last one (invariant 3's habit, applied to stock rather than to money).
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a quantity on hand answerable. <see cref="StockLevel.QuantityOnHand"/> is a
/// running total, and a total with nothing behind it is a number a storeman cannot argue with;
/// <see cref="QuantityOnHandAfter"/> is stamped on every line, so the ledger reads down the page and
/// the disagreement between a count and the system has a place it starts.
/// </para>
/// <para>
/// Deliberately not a replacement for the audit trail. The audit entry (invariant 1) is the
/// tamper-evident administrative record of a write, held in the platform schema and filtered by
/// action; this is the store's own record of the stock. Both are written in the same transaction, so
/// neither can exist without the other — the same split as <c>assets.asset_history</c>.
/// </para>
/// </remarks>
public sealed class StockMovement
{
    /// <summary>Longest note recorded against a movement.</summary>
    public const int NoteLength = 1024;

    /// <summary>Longest external reference recorded against a movement — a delivery note, a docket.</summary>
    public const int ReferenceLength = 128;

    private StockMovement()
    {
        // EF materialisation.
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this movement. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The catalogue item that moved.</summary>
    public Guid StockItemId { get; private init; }

    /// <summary>The warehouse it moved in or out of.</summary>
    public Guid WarehouseId { get; private init; }

    /// <summary>Which way, and why.</summary>
    public StockMovementType MovementType { get; private init; }

    /// <summary>
    /// Signed: positive on a receipt, negative on an issue, either way on an adjustment. Signed
    /// rather than a magnitude beside the type, so summing the ledger reproduces the quantity on
    /// hand without a lookup table of which types count as which direction.
    /// </summary>
    public decimal QuantityChange { get; private init; }

    /// <summary>What was on the shelf once this line had been applied.</summary>
    public decimal QuantityOnHandAfter { get; private init; }

    /// <summary>What one unit cost on a receipt, where the receipt carried a price. Money is <see langword="decimal"/>.</summary>
    public decimal? UnitCost { get; private init; }

    /// <summary>The paperwork this came off — a delivery note number, a docket.</summary>
    public string? Reference { get; private init; }

    /// <summary>
    /// The job the parts went to, on an issue. A plain Guid with no foreign key: Work Orders is
    /// another module and another schema, so the database cannot enforce it and this module must
    /// never query that table. WP-3.3 stamps it; a screen showing the job resolves it through that
    /// module's service.
    /// </summary>
    public Guid? WorkOrderId { get; private init; }

    /// <summary>Why, or what happened, in the storeman's words. Required on an adjustment.</summary>
    public string? Note { get; private init; }

    /// <summary>Subject id of whoever moved it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>When it moved.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>What this line was worth, where a unit cost was recorded.</summary>
    public decimal? Value => UnitCost is { } cost ? cost * QuantityChange : null;

    /// <summary>Records stock coming in.</summary>
    internal static StockMovement Receipt(
        Guid stockItemId,
        Guid warehouseId,
        decimal quantity,
        decimal quantityOnHandAfter,
        decimal? unitCost,
        string? reference,
        string? note,
        RegistryActor actor,
        DateTimeOffset now) =>
        Line(stockItemId, warehouseId, StockMovementType.Receipt, quantity, quantityOnHandAfter, actor, now, note, unitCost, reference);

    /// <summary>Records stock going out to a job.</summary>
    internal static StockMovement Issue(
        Guid stockItemId,
        Guid warehouseId,
        decimal quantity,
        decimal quantityOnHandAfter,
        Guid? workOrderId,
        string? reference,
        string? note,
        RegistryActor actor,
        DateTimeOffset now) =>
        Line(
            stockItemId,
            warehouseId,
            StockMovementType.Issue,
            -quantity,
            quantityOnHandAfter,
            actor,
            now,
            note,
            reference: reference,
            workOrderId: workOrderId);

    /// <summary>Records a count correction. The reason is not optional here.</summary>
    internal static StockMovement Adjustment(
        Guid stockItemId,
        Guid warehouseId,
        decimal change,
        decimal quantityOnHandAfter,
        string reason,
        RegistryActor actor,
        DateTimeOffset now) =>
        Line(stockItemId, warehouseId, StockMovementType.Adjustment, change, quantityOnHandAfter, actor, now, RequireReason(reason));

    /// <summary>
    /// Checks an adjustment says why. Called by <see cref="StockItem.Adjust"/> before it touches a
    /// quantity, and again here for anything that ever builds a line another way.
    /// </summary>
    /// <remarks>
    /// Invariant 5: a sensitive action is permission-gated AND audited, and an audit entry that does
    /// not say why is a row nobody can act on. A stock take that moves a count without a reason is
    /// exactly the write an auditor comes looking for.
    /// </remarks>
    /// <exception cref="InventoryValidationException">No reason was given.</exception>
    internal static string RequireReason(string reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? throw new InventoryValidationException("An adjustment must say why the count is being corrected.")
            : reason;

    /// <summary>Checks a movement says who made it.</summary>
    /// <exception cref="InventoryValidationException">The actor carries no subject id.</exception>
    internal static RegistryActor RequireActor(RegistryActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (string.IsNullOrWhiteSpace(actor.Id))
        {
            throw new InventoryValidationException("A stock movement must name who moved it.");
        }

        return actor;
    }

    private static StockMovement Line(
        Guid stockItemId,
        Guid warehouseId,
        StockMovementType movementType,
        decimal quantityChange,
        decimal quantityOnHandAfter,
        RegistryActor actor,
        DateTimeOffset now,
        string? note = null,
        decimal? unitCost = null,
        string? reference = null,
        Guid? workOrderId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return new StockMovement
        {
            Id = Guid.CreateVersion7(now),
            StockItemId = stockItemId,
            WarehouseId = warehouseId,
            MovementType = movementType,
            QuantityChange = quantityChange,
            QuantityOnHandAfter = quantityOnHandAfter,
            UnitCost = unitCost,
            Reference = RegistryText.Clean(reference, ReferenceLength),
            WorkOrderId = workOrderId,
            Note = RegistryText.Clean(note, NoteLength),
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new InventoryValidationException("A stock movement must name who moved it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            RecordedAt = now,
        };
    }
}
