using GridCore.Modules.Inventory.Data;
using GridCore.Modules.Inventory.Features.Shared;
using GridCore.Modules.Inventory.Features.Warehouses;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Inventory.Features.Items;

/// <summary>The catalogue details a caller may set or correct. Shared by registration and update.</summary>
public interface IStockItemDetails
{
    /// <summary>What kind of thing it is.</summary>
    StockItemCategory Category { get; }

    /// <summary>What it is.</summary>
    string Name { get; }

    /// <summary>What one of it is.</summary>
    UnitOfMeasure Unit { get; }

    /// <summary>Anything more a storeman needs to know before issuing it.</summary>
    string? Description { get; }

    /// <summary>The manufacturer's part number, where the item has one.</summary>
    string? ManufacturerPartNumber { get; }

    /// <summary>What one unit is reckoned to cost.</summary>
    decimal UnitCost { get; }
}

/// <summary>What a caller supplies to enter an item in the catalogue.</summary>
/// <param name="Category">What kind of thing it is.</param>
/// <param name="Name">What it is.</param>
/// <param name="Unit">What one of it is.</param>
/// <param name="Description">Anything more a storeman needs to know.</param>
/// <param name="ManufacturerPartNumber">The manufacturer's part number.</param>
/// <param name="UnitCost">What one unit is reckoned to cost.</param>
public sealed record RegisterStockItemInput(
    StockItemCategory Category,
    string Name,
    UnitOfMeasure Unit,
    string? Description = null,
    string? ManufacturerPartNumber = null,
    decimal UnitCost = 0m) : IStockItemDetails;

/// <summary>What a caller supplies to correct a catalogue line.</summary>
/// <param name="Category">What kind of thing it is.</param>
/// <param name="Name">What it is.</param>
/// <param name="Unit">What one of it is. Fixed once stock has moved.</param>
/// <param name="UnitCost">What one unit is reckoned to cost.</param>
/// <param name="IsActive">Whether the store still carries this line.</param>
/// <param name="Description">Anything more a storeman needs to know.</param>
/// <param name="ManufacturerPartNumber">The manufacturer's part number.</param>
/// <param name="StatusReason">Why it is being discontinued or brought back. Recorded only when the flag moves.</param>
public sealed record UpdateStockItemInput(
    StockItemCategory Category,
    string Name,
    UnitOfMeasure Unit,
    decimal UnitCost,
    bool IsActive = true,
    string? Description = null,
    string? ManufacturerPartNumber = null,
    string? StatusReason = null) : IStockItemDetails;

/// <summary>What a caller supplies to book stock in.</summary>
/// <param name="WarehouseId">Where it landed.</param>
/// <param name="Quantity">How much came in.</param>
/// <param name="UnitCost">What one unit cost on this delivery.</param>
/// <param name="Reference">The delivery note or docket it came off.</param>
/// <param name="Note">Anything the storeman wants on the ledger line.</param>
public sealed record ReceiveStockInput(
    Guid WarehouseId,
    decimal Quantity,
    decimal? UnitCost = null,
    string? Reference = null,
    string? Note = null);

/// <summary>What a caller supplies to issue stock to a job.</summary>
/// <param name="WarehouseId">Which shelf it came off.</param>
/// <param name="Quantity">How much went out.</param>
/// <param name="WorkOrderId">The job it went to, where there is one. WP-3.3 supplies it.</param>
/// <param name="Reference">The docket it went out on.</param>
/// <param name="Note">Anything the storeman wants on the ledger line.</param>
public sealed record IssueStockInput(
    Guid WarehouseId,
    decimal Quantity,
    Guid? WorkOrderId = null,
    string? Reference = null,
    string? Note = null);

/// <summary>What a caller supplies to correct a count.</summary>
/// <param name="WarehouseId">Which shelf was counted.</param>
/// <param name="CountedQuantity">What was actually on it. The difference is derived.</param>
/// <param name="Reason">Why the count is being corrected. Required — invariant 5.</param>
public sealed record AdjustStockInput(Guid WarehouseId, decimal CountedQuantity, string Reason);

/// <summary>What a caller supplies to set a reorder level.</summary>
/// <param name="WarehouseId">Which shelf.</param>
/// <param name="MinimumQuantity">How low it may fall. Zero clears the level.</param>
public sealed record SetMinimumQuantityInput(Guid WarehouseId, decimal MinimumQuantity);

/// <summary>How the catalogue list is filtered.</summary>
/// <param name="Search">Matched against the code, the name and the part number, case-insensitively.</param>
/// <param name="Category">Only items of this kind.</param>
/// <param name="WarehouseId">Only items stocked in this warehouse.</param>
/// <param name="BelowMinimum">Only items at or below a reorder level — the low-stock report.</param>
/// <param name="IncludeInactive">Whether discontinued lines are included.</param>
/// <param name="Limit">Most rows to return.</param>
public sealed record StockItemQuery(
    string? Search = null,
    StockItemCategory? Category = null,
    Guid? WarehouseId = null,
    bool BelowMinimum = false,
    bool IncludeInactive = false,
    int Limit = 50);

/// <summary>How one item's ledger is filtered.</summary>
/// <param name="WarehouseId">Only movements in this warehouse.</param>
/// <param name="MovementType">Only movements of this kind.</param>
/// <param name="Limit">Most lines to return, newest first.</param>
public sealed record StockMovementQuery(Guid? WarehouseId = null, StockMovementType? MovementType = null, int Limit = 100);

/// <summary>The store. Endpoints are a thin layer over it.</summary>
public interface IStockItemService
{
    /// <summary>Enters an item in the catalogue, issuing the next code.</summary>
    Task<StockItem> RegisterAsync(RegisterStockItemInput input, CancellationToken cancellationToken = default);

    /// <summary>Corrects a catalogue line. Not its code, and not its quantities.</summary>
    Task<StockItem> UpdateAsync(Guid id, UpdateStockItemInput input, CancellationToken cancellationToken = default);

    /// <summary>Books stock in at a warehouse.</summary>
    Task<StockMovement> ReceiveAsync(Guid id, ReceiveStockInput input, CancellationToken cancellationToken = default);

    /// <summary>Issues stock out of a warehouse to a job.</summary>
    Task<StockMovement> IssueAsync(Guid id, IssueStockInput input, CancellationToken cancellationToken = default);

    /// <summary>Corrects the count in a warehouse. Sensitive: gated on <c>inventory.adjust</c> and audited.</summary>
    Task<StockMovement> AdjustAsync(Guid id, AdjustStockInput input, CancellationToken cancellationToken = default);

    /// <summary>Sets how low an item may fall in a warehouse before somebody reorders.</summary>
    Task<StockItem> SetMinimumQuantityAsync(Guid id, SetMinimumQuantityInput input, CancellationToken cancellationToken = default);

    /// <summary>One item with its levels and its ledger, or <see langword="null"/> if there is no such id.</summary>
    Task<StockItem?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The catalogue list with the levels of each item, newest first.</summary>
    Task<IReadOnlyList<StockItem>> ListAsync(StockItemQuery query, CancellationToken cancellationToken = default);

    /// <summary>One item's ledger, newest first, optionally narrowed.</summary>
    /// <exception cref="StockItemNotFoundException">There is no item with that id.</exception>
    Task<IReadOnlyList<StockMovement>> MovementsAsync(
        Guid id,
        StockMovementQuery? query = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The store over the inventory schema.</summary>
/// <remarks>
/// Every write runs inside <see cref="IUnitOfWork.ExecuteAsync"/> and never calls
/// <c>SaveChanges</c> itself, so the level, its ledger line and its audit entry are one transaction
/// — invariant 1. The ledger line is written by the aggregate rather than here, which is what makes
/// "the count moved but nothing recorded why" impossible.
/// </remarks>
public sealed class StockItemService(
    InventoryDbContext database,
    IStockItemNumberGenerator codes,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider clock) : IStockItemService
{
    /// <summary>The largest page <see cref="ListAsync"/> will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>The largest ledger page <see cref="MovementsAsync"/> will return.</summary>
    public const int MaxLedgerPageSize = 500;

    /// <inheritdoc />
    public Task<StockItem> RegisterAsync(RegisterStockItemInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                await RequirePartNumberIsFreeAsync(input.ManufacturerPartNumber, excluding: null, ct).ConfigureAwait(false);

                var itemCode = await codes.NextItemCodeAsync(ct).ConfigureAwait(false);

                // The unique index is the real guarantee; this turns the loser of a race into a 409
                // the caller can retry rather than a 500 out of the database.
                if (await database.StockItems.AnyAsync(existing => existing.ItemCode == itemCode, ct).ConfigureAwait(false))
                {
                    throw new InventoryWorkflowException(
                        $"Item code {itemCode} has just been taken by another registration. Try again.");
                }

                var item = StockItem.Register(
                    itemCode,
                    input.Category,
                    input.Name,
                    input.Unit,
                    now,
                    input.Description,
                    input.ManufacturerPartNumber,
                    input.UnitCost);

                database.StockItems.Add(item);

                audit.Record(
                    AuditActions.StockItemRegistered,
                    AuditEntityTypes.StockItem,
                    item.Id.ToString(),
                    before: null,
                    after: StockItemSnapshot.Of(item));

                // No event. A catalogue line is this module's own record and nothing outside it acts
                // on one existing; the fact other modules wait for is GoodsReceived, which needs the
                // purchase order and vendor WP-4.1 introduces.
                return item;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<StockItem> UpdateAsync(Guid id, UpdateStockItemInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var item = await LoadAsync(id, ct).ConfigureAwait(false);
                var before = StockItemSnapshot.Of(item);

                await RequirePartNumberIsFreeAsync(input.ManufacturerPartNumber, excluding: item.Id, ct).ConfigureAwait(false);

                item.UpdateDetails(
                    input.Category,
                    input.Name,
                    input.Unit,
                    input.UnitCost,
                    input.IsActive,
                    input.Description,
                    input.ManufacturerPartNumber,
                    input.StatusReason);

                audit.Record(
                    AuditActions.StockItemUpdated,
                    AuditEntityTypes.StockItem,
                    item.Id.ToString(),
                    before,
                    StockItemSnapshot.Of(item));

                return item;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<StockMovement> ReceiveAsync(Guid id, ReceiveStockInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var item = await LoadAsync(id, ct).ConfigureAwait(false);
                var warehouse = await RequireWarehouseAsync(input.WarehouseId, mustBeOpen: true, ct).ConfigureAwait(false);
                var before = StockLevelSnapshot.Of(item, warehouse);

                var movement = item.Receive(
                    warehouse.Id,
                    input.Quantity,
                    RegistryActor.Of(currentUser),
                    now,
                    input.UnitCost,
                    input.Reference,
                    input.Note);

                Audited(AuditActions.StockReceived, item, warehouse, before, movement);

                return movement;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<StockMovement> IssueAsync(Guid id, IssueStockInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var item = await LoadAsync(id, ct).ConfigureAwait(false);

                // Issuing from a warehouse that is closing is allowed, exactly as issuing a
                // discontinued item is: the stock on those shelves still has to go somewhere, and
                // refusing would strand it until somebody reopened the store.
                var warehouse = await RequireWarehouseAsync(input.WarehouseId, mustBeOpen: false, ct).ConfigureAwait(false);
                var before = StockLevelSnapshot.Of(item, warehouse);

                var movement = item.Issue(
                    warehouse.Id,
                    input.Quantity,
                    RegistryActor.Of(currentUser),
                    now,
                    input.WorkOrderId,
                    input.Reference,
                    input.Note);

                Audited(AuditActions.StockIssued, item, warehouse, before, movement);

                return movement;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<StockMovement> AdjustAsync(Guid id, AdjustStockInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var item = await LoadAsync(id, ct).ConfigureAwait(false);
                var warehouse = await RequireWarehouseAsync(input.WarehouseId, mustBeOpen: false, ct).ConfigureAwait(false);
                var before = StockLevelSnapshot.Of(item, warehouse);

                var movement = item.Adjust(
                    warehouse.Id,
                    input.CountedQuantity,
                    input.Reason,
                    RegistryActor.Of(currentUser),
                    now);

                // The sensitive one (invariant 5): permission-gated at the endpoint on
                // inventory.adjust, and audited here with the count before and after, so the entry
                // reads as "was 40, counted 32, because …" rather than merely "stock was adjusted".
                Audited(AuditActions.StockAdjusted, item, warehouse, before, movement);

                return movement;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<StockItem> SetMinimumQuantityAsync(Guid id, SetMinimumQuantityInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var item = await LoadAsync(id, ct).ConfigureAwait(false);
                var warehouse = await RequireWarehouseAsync(input.WarehouseId, mustBeOpen: true, ct).ConfigureAwait(false);
                var before = StockLevelSnapshot.Of(item, warehouse);

                item.SetMinimumQuantity(warehouse.Id, input.MinimumQuantity, now);

                // Audited even though nothing moved: raising a reorder level is how a low-stock
                // report is quietly silenced, and that is precisely the change somebody later wants
                // to be able to look up.
                audit.Record(
                    AuditActions.StockMinimumSet,
                    AuditEntityTypes.StockItem,
                    item.Id.ToString(),
                    before,
                    StockLevelSnapshot.Of(item, warehouse));

                return item;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<StockItem?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.StockItems
            .Include(item => item.Levels)
            .Include(item => item.Movements)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<StockItem>> ListAsync(StockItemQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The levels are included and the ledger is not: a list row shows what the store holds and
        // where, which is three small rows an item, where the movements behind them are unbounded.
        var items = database.StockItems
            .AsNoTracking()
            .Include(item => item.Levels)
            .AsQueryable();

        if (!query.IncludeInactive)
        {
            items = items.Where(item => item.IsActive);
        }

        // Matched against a non-nullable local: the column is stored by name, and EF cannot
        // translate a nullable-to-converted-value comparison.
        if (query.Category is { } category)
        {
            items = items.Where(item => item.Category == category);
        }

        // The two level filters compose deliberately rather than independently: "low stock in the
        // north depot" has to mean low *there*, not "stocked there and low anywhere".
        items = (query.WarehouseId, query.BelowMinimum) switch
        {
            ({ } warehouseId, true) => items.Where(item =>
                item.Levels.AsQueryable().Where(level => level.WarehouseId == warehouseId).Any(StockLevel.BelowMinimum)),
            ({ } warehouseId, false) => items.Where(item =>
                item.Levels.Any(level => level.WarehouseId == warehouseId)),
            (null, true) => items.Where(item => item.Levels.AsQueryable().Any(StockLevel.BelowMinimum)),
            (null, false) => items,
        };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Lower-cased on both sides rather than ILIKE, so the fast tier exercises the same SQL
            // shape production runs. A storeman searches by whatever is legible — the bin label, the
            // name on the requisition, or the number printed on the box.
            var term = query.Search.Trim().ToLowerInvariant();

            items = items.Where(item =>
                item.ItemCode.ToLower().Contains(term)
                || item.Name.ToLower().Contains(term)
                || (item.ManufacturerPartNumber != null && item.ManufacturerPartNumber.ToLower().Contains(term)));
        }

        // Ordered by key: ids are Guid v7, so the primary-key index already orders chronologically
        // on Postgres and on the fast tier's SQLite alike.
        return await items
            .OrderByDescending(item => item.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StockMovement>> MovementsAsync(
        Guid id,
        StockMovementQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new StockMovementQuery();

        if (!await database.StockItems.AnyAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false))
        {
            // Distinguished from an item that has simply never moved, which is a real state — a
            // catalogue line registered and not yet delivered — where an empty list for a missing id
            // would say the item existed.
            throw new StockItemNotFoundException(id);
        }

        var movements = database.StockMovements
            .AsNoTracking()
            .Where(movement => movement.StockItemId == id);

        if (query.WarehouseId is { } warehouseId)
        {
            movements = movements.Where(movement => movement.WarehouseId == warehouseId);
        }

        if (query.MovementType is { } movementType)
        {
            movements = movements.Where(movement => movement.MovementType == movementType);
        }

        // Newest first, unlike an asset's history: a ledger is read from the most recent movement
        // back, because the question is nearly always "what happened to this count lately".
        return await movements
            .OrderByDescending(movement => movement.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxLedgerPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private void Audited(
        string action,
        StockItem item,
        Warehouse warehouse,
        StockLevelSnapshot before,
        StockMovement movement) =>
        audit.Record(
            action,
            AuditEntityTypes.StockItem,
            item.Id.ToString(),
            before,
            StockMovementSnapshot.Of(movement, warehouse));

    private async Task<StockItem> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await database.StockItems
            .Include(item => item.Levels)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false)
        ?? throw new StockItemNotFoundException(id);

    private async Task<Warehouse> RequireWarehouseAsync(Guid warehouseId, bool mustBeOpen, CancellationToken cancellationToken)
    {
        // The warehouse is reference data in this module's own schema, so it is read here rather
        // than passed into the aggregate: the aggregate's rules are about the item and the quantity,
        // and a warehouse it cannot see is one it cannot get wrong.
        var warehouse = await database.Warehouses
            .FirstOrDefaultAsync(candidate => candidate.Id == warehouseId, cancellationToken).ConfigureAwait(false)
            ?? throw new WarehouseNotFoundException(warehouseId);

        if (mustBeOpen && !warehouse.IsActive)
        {
            throw new InventoryWorkflowException(
                $"Warehouse {warehouse.Code} is closed; stock cannot be received into it or reserved there.");
        }

        return warehouse;
    }

    private async Task RequirePartNumberIsFreeAsync(string? partNumber, Guid? excluding, CancellationToken cancellationToken)
    {
        var number = RegistryText.Clean(partNumber, StockItem.PartNumberLength);

        if (number is null)
        {
            // Plenty of stock carries no manufacturer's number — cut conductor, fixings by the kilo
            // — and the unique index treats NULLs as distinct, so any number of them coexist.
            return;
        }

        var taken = await database.StockItems
            .Where(existing => existing.ManufacturerPartNumber == number)
            .Where(existing => excluding == null || existing.Id != excluding)
            .Select(existing => existing.ItemCode)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (taken is not null)
        {
            // The unique index is what actually guarantees this; the check is here so the second
            // catalogue line for one part reads as a conflict naming the item it collides with,
            // rather than a 500.
            throw new InventoryWorkflowException(
                $"Manufacturer's part number '{number}' is already held as item {taken}.");
        }
    }
}

/// <summary>
/// The before/after shape a catalogue line is audited as. A dedicated record rather than the entity,
/// so changing the entity later cannot silently change the meaning of historic entries.
/// </summary>
/// <param name="Id">Which item.</param>
/// <param name="ItemCode">Its catalogue code.</param>
/// <param name="Name">What it is.</param>
/// <param name="Category">What kind of thing it is.</param>
/// <param name="Unit">What one of it is.</param>
/// <param name="Description">What a storeman is told about it.</param>
/// <param name="ManufacturerPartNumber">The manufacturer's part number.</param>
/// <param name="UnitCost">What one unit is reckoned to cost.</param>
/// <param name="IsActive">Whether the store still carries it.</param>
/// <param name="StatusReason">Why it was last discontinued or brought back.</param>
/// <param name="TotalOnHand">How much is held across every warehouse.</param>
public sealed record StockItemSnapshot(
    Guid Id,
    string ItemCode,
    string Name,
    StockItemCategory Category,
    UnitOfMeasure Unit,
    string? Description,
    string? ManufacturerPartNumber,
    decimal UnitCost,
    bool IsActive,
    string? StatusReason,
    decimal TotalOnHand)
{
    /// <summary>Takes a snapshot of <paramref name="item"/> as it stands.</summary>
    public static StockItemSnapshot Of(StockItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new StockItemSnapshot(
            item.Id,
            item.ItemCode,
            item.Name,
            item.Category,
            item.Unit,
            item.Description,
            item.ManufacturerPartNumber,
            item.UnitCost,
            item.IsActive,
            item.StatusReason,
            item.TotalOnHand);
    }
}

/// <summary>What one shelf held, as the audit trail records it.</summary>
/// <param name="StockItemId">Which item.</param>
/// <param name="ItemCode">Its catalogue code, so the entry reads without a lookup.</param>
/// <param name="WarehouseId">Which warehouse.</param>
/// <param name="WarehouseCode">Its code, for the same reason.</param>
/// <param name="QuantityOnHand">How much was on the shelf.</param>
/// <param name="MinimumQuantity">The reorder level in force.</param>
public sealed record StockLevelSnapshot(
    Guid StockItemId,
    string ItemCode,
    Guid WarehouseId,
    string WarehouseCode,
    decimal QuantityOnHand,
    decimal MinimumQuantity)
{
    /// <summary>Takes a snapshot of what <paramref name="item"/> holds in <paramref name="warehouse"/>.</summary>
    public static StockLevelSnapshot Of(StockItem item, Warehouse warehouse)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(warehouse);

        var level = item.LevelIn(warehouse.Id);

        return new StockLevelSnapshot(
            item.Id,
            item.ItemCode,
            warehouse.Id,
            warehouse.Code,
            level?.QuantityOnHand ?? 0m,
            level?.MinimumQuantity ?? 0m);
    }
}

/// <summary>What a movement did, as the audit trail records it.</summary>
/// <param name="Id">Which ledger line.</param>
/// <param name="MovementType">Which way, and why.</param>
/// <param name="WarehouseId">Which warehouse.</param>
/// <param name="WarehouseCode">Its code, so the entry reads without a lookup.</param>
/// <param name="QuantityChange">How much moved, signed.</param>
/// <param name="QuantityOnHandAfter">What was left afterwards.</param>
/// <param name="UnitCost">What one unit cost, on a receipt that carried a price.</param>
/// <param name="Reference">The paperwork it came off.</param>
/// <param name="WorkOrderId">The job it went to, on an issue.</param>
/// <param name="Note">Why, or what happened.</param>
public sealed record StockMovementSnapshot(
    Guid Id,
    StockMovementType MovementType,
    Guid WarehouseId,
    string WarehouseCode,
    decimal QuantityChange,
    decimal QuantityOnHandAfter,
    decimal? UnitCost,
    string? Reference,
    Guid? WorkOrderId,
    string? Note)
{
    /// <summary>Takes a snapshot of <paramref name="movement"/>.</summary>
    public static StockMovementSnapshot Of(StockMovement movement, Warehouse warehouse)
    {
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(warehouse);

        return new StockMovementSnapshot(
            movement.Id,
            movement.MovementType,
            warehouse.Id,
            warehouse.Code,
            movement.QuantityChange,
            movement.QuantityOnHandAfter,
            movement.UnitCost,
            movement.Reference,
            movement.WorkOrderId,
            movement.Note);
    }
}
