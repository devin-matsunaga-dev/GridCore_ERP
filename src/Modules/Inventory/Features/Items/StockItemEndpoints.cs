using GridCore.Modules.Inventory.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Inventory.Features.Items;

/// <summary>A request that names the warehouse it applies to. Every movement does.</summary>
public interface IStockMovementRequest
{
    /// <summary>The warehouse the movement is in.</summary>
    Guid WarehouseId { get; }
}

/// <summary>Body of a request to enter an item in the catalogue.</summary>
/// <param name="Category">What kind of thing it is.</param>
/// <param name="Name">What it is.</param>
/// <param name="Unit">What one of it is.</param>
/// <param name="Description">Anything more a storeman needs to know.</param>
/// <param name="ManufacturerPartNumber">The manufacturer's part number, where the item has one.</param>
/// <param name="UnitCost">What one unit is reckoned to cost.</param>
public sealed record RegisterStockItemRequest(
    StockItemCategory Category,
    string Name,
    UnitOfMeasure Unit,
    string? Description = null,
    string? ManufacturerPartNumber = null,
    decimal UnitCost = 0m) : IStockItemDetails;

/// <summary>Body of a request to correct a catalogue line.</summary>
/// <param name="Category">What kind of thing it is.</param>
/// <param name="Name">What it is.</param>
/// <param name="Unit">What one of it is. Fixed once stock has moved.</param>
/// <param name="UnitCost">What one unit is reckoned to cost.</param>
/// <param name="IsActive">Whether the store still carries this line.</param>
/// <param name="Description">Anything more a storeman needs to know.</param>
/// <param name="ManufacturerPartNumber">The manufacturer's part number.</param>
/// <param name="StatusReason">Why it is being discontinued or brought back.</param>
public sealed record UpdateStockItemRequest(
    StockItemCategory Category,
    string Name,
    UnitOfMeasure Unit,
    decimal UnitCost,
    bool IsActive = true,
    string? Description = null,
    string? ManufacturerPartNumber = null,
    string? StatusReason = null) : IStockItemDetails;

/// <summary>Body of a request to book stock in.</summary>
/// <param name="WarehouseId">Where it landed.</param>
/// <param name="Quantity">How much came in.</param>
/// <param name="UnitCost">What one unit cost on this delivery.</param>
/// <param name="Reference">The delivery note it came off.</param>
/// <param name="Note">Anything for the ledger line.</param>
public sealed record ReceiveStockRequest(
    Guid WarehouseId,
    decimal Quantity,
    decimal? UnitCost = null,
    string? Reference = null,
    string? Note = null) : IStockMovementRequest;

/// <summary>Body of a request to issue stock to a job.</summary>
/// <param name="WarehouseId">Which shelf it came off.</param>
/// <param name="Quantity">How much went out.</param>
/// <param name="WorkOrderId">The job it went to, where there is one.</param>
/// <param name="Reference">The docket it went out on.</param>
/// <param name="Note">Anything for the ledger line.</param>
public sealed record IssueStockRequest(
    Guid WarehouseId,
    decimal Quantity,
    Guid? WorkOrderId = null,
    string? Reference = null,
    string? Note = null) : IStockMovementRequest;

/// <summary>Body of a request to correct a count.</summary>
/// <param name="WarehouseId">Which shelf was counted.</param>
/// <param name="CountedQuantity">What was actually on it. The difference is derived.</param>
/// <param name="Reason">Why the count is being corrected. Required.</param>
public sealed record AdjustStockRequest(Guid WarehouseId, decimal CountedQuantity, string Reason) : IStockMovementRequest;

/// <summary>Body of a request to set a reorder level.</summary>
/// <param name="WarehouseId">Which shelf.</param>
/// <param name="MinimumQuantity">How low it may fall. Zero clears the level.</param>
public sealed record SetMinimumQuantityRequest(Guid WarehouseId, decimal MinimumQuantity) : IStockMovementRequest;

/// <summary>What one warehouse holds of one item, as the API returns it.</summary>
/// <param name="WarehouseId">Which warehouse.</param>
/// <param name="QuantityOnHand">How much is on the shelf.</param>
/// <param name="MinimumQuantity">The reorder level, zero where none is set.</param>
/// <param name="IsBelowMinimum">Whether this shelf is at or below its reorder level.</param>
/// <param name="LastMovedAt">When stock last moved here.</param>
public sealed record StockLevelResponse(
    Guid WarehouseId,
    decimal QuantityOnHand,
    decimal MinimumQuantity,
    bool IsBelowMinimum,
    DateTimeOffset? LastMovedAt)
{
    /// <summary>Projects a level for the wire.</summary>
    public static StockLevelResponse From(StockLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);

        return new StockLevelResponse(
            level.WarehouseId,
            level.QuantityOnHand,
            level.MinimumQuantity,
            level.IsBelowMinimum,
            level.LastMovedAt);
    }
}

/// <summary>One line of an item's stock ledger as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="MovementType">Which way, and why.</param>
/// <param name="WarehouseId">Which warehouse.</param>
/// <param name="QuantityChange">How much moved, signed.</param>
/// <param name="QuantityOnHandAfter">What was left afterwards.</param>
/// <param name="UnitCost">What one unit cost, on a receipt that carried a price.</param>
/// <param name="Value">What the line was worth, where a cost was recorded.</param>
/// <param name="Reference">The paperwork it came off.</param>
/// <param name="WorkOrderId">The job it went to, on an issue.</param>
/// <param name="Note">Why, or what happened.</param>
/// <param name="ActorId">Subject id of whoever moved it.</param>
/// <param name="ActorName">Their name at the time.</param>
/// <param name="RecordedAt">When.</param>
public sealed record StockMovementResponse(
    Guid Id,
    string MovementType,
    Guid WarehouseId,
    decimal QuantityChange,
    decimal QuantityOnHandAfter,
    decimal? UnitCost,
    decimal? Value,
    string? Reference,
    Guid? WorkOrderId,
    string? Note,
    string ActorId,
    string? ActorName,
    DateTimeOffset RecordedAt)
{
    /// <summary>Projects a movement for the wire.</summary>
    public static StockMovementResponse From(StockMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);

        return new StockMovementResponse(
            movement.Id,
            movement.MovementType.ToString(),
            movement.WarehouseId,
            movement.QuantityChange,
            movement.QuantityOnHandAfter,
            movement.UnitCost,
            movement.Value,
            movement.Reference,
            movement.WorkOrderId,
            movement.Note,
            movement.ActorId,
            movement.ActorName,
            movement.RecordedAt);
    }
}

/// <summary>A catalogue item as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="ItemCode">The code on the bin label.</param>
/// <param name="Name">What it is.</param>
/// <param name="Category">What kind of thing it is.</param>
/// <param name="Unit">What one of it is.</param>
/// <param name="Description">What a storeman is told about it.</param>
/// <param name="ManufacturerPartNumber">The manufacturer's part number.</param>
/// <param name="UnitCost">What one unit is reckoned to cost.</param>
/// <param name="IsActive">Whether the store still carries it.</param>
/// <param name="StatusReason">Why it was last discontinued or brought back.</param>
/// <param name="TotalOnHand">How much is held across every warehouse.</param>
/// <param name="IsBelowMinimum">Whether any warehouse is at or below its reorder level.</param>
/// <param name="RegisteredAt">When it was entered in the catalogue.</param>
/// <param name="Levels">What each warehouse holds.</param>
/// <param name="Movements">The ledger, newest first. Empty on a list row.</param>
public sealed record StockItemResponse(
    Guid Id,
    string ItemCode,
    string Name,
    string Category,
    string Unit,
    string? Description,
    string? ManufacturerPartNumber,
    decimal UnitCost,
    bool IsActive,
    string? StatusReason,
    decimal TotalOnHand,
    bool IsBelowMinimum,
    DateTimeOffset RegisteredAt,
    IReadOnlyList<StockLevelResponse> Levels,
    IReadOnlyList<StockMovementResponse> Movements)
{
    /// <summary>Projects a <see cref="StockItem"/> for the wire, with whatever is loaded.</summary>
    public static StockItemResponse From(StockItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new StockItemResponse(
            item.Id,
            item.ItemCode,
            item.Name,
            item.Category.ToString(),
            item.Unit.ToString(),
            item.Description,
            item.ManufacturerPartNumber,
            item.UnitCost,
            item.IsActive,
            item.StatusReason,
            item.TotalOnHand,
            item.IsBelowMinimum,
            item.RegisteredAt,
            item.Levels
                .OrderBy(level => level.WarehouseId)
                .Select(StockLevelResponse.From)
                .ToList(),
            item.Movements
                .OrderByDescending(movement => movement.Id)
                .Select(StockMovementResponse.From)
                .ToList());
    }
}

/// <summary>The store's HTTP surface.</summary>
public static class StockItemEndpoints
{
    /// <summary>Route prefix of the catalogue.</summary>
    public const string RoutePrefix = "/api/inventory/items";

    /// <summary>Maps the stock endpoints.</summary>
    public static IEndpointRouteBuilder MapStockItemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Inventory");

        group
            .MapGet("/", async (
                    string? search,
                    StockItemCategory? category,
                    Guid? warehouseId,
                    bool? belowMinimum,
                    bool? includeInactive,
                    int? limit,
                    [FromServices] IStockItemService stock,
                    CancellationToken cancellationToken) =>
                Results.Ok((await stock.ListAsync(
                        new StockItemQuery(
                            search,
                            category,
                            warehouseId,
                            belowMinimum ?? false,
                            includeInactive ?? false,
                            limit ?? 50),
                        cancellationToken))
                    .Select(StockItemResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Inventory.Read)
            .WithName("ListStockItems");

        group
            .MapGet("/{id:guid}", async ([FromRoute] Guid id, [FromServices] IStockItemService stock, CancellationToken cancellationToken) =>
            {
                var item = await stock.FindAsync(id, cancellationToken);

                return item is null ? InventoryProblems.StockItemNotFound(id) : Results.Ok(StockItemResponse.From(item));
            })
            .RequirePermission(Permissions.Inventory.Read)
            .WithName("GetStockItem");

        // The stock ledger. Its own resource rather than a field of the item, because it is a list
        // that grows with every delivery and every job — and the filters are what narrow it to the
        // issues WP-3.3 writes or the receipts WP-4.1 does.
        group
            .MapGet("/{id:guid}/movements", (
                    [FromRoute] Guid id,
                    Guid? warehouseId,
                    StockMovementType? movementType,
                    int? limit,
                    [FromServices] IStockItemService stock,
                    CancellationToken cancellationToken) =>
                InventoryProblems.RunAsync(async () =>
                    Results.Ok((await stock.MovementsAsync(
                            id,
                            new StockMovementQuery(warehouseId, movementType, limit ?? 100),
                            cancellationToken))
                        .Select(StockMovementResponse.From)
                        .ToList())))
            .RequirePermission(Permissions.Inventory.Read)
            .WithName("GetStockItemMovements");

        group
            .MapPost("/", (RegisterStockItemRequest body, [FromServices] IStockItemService stock, CancellationToken cancellationToken) =>
                InventoryProblems.RunAsync(async () =>
                {
                    var item = await stock.RegisterAsync(
                        new RegisterStockItemInput(
                            body.Category,
                            body.Name,
                            body.Unit,
                            body.Description,
                            body.ManufacturerPartNumber,
                            body.UnitCost),
                        cancellationToken);

                    return Results.Created($"{RoutePrefix}/{item.Id}", StockItemResponse.From(item));
                }))
            .RequirePermission(Permissions.Inventory.Write)
            .WithValidation<RegisterStockItemRequest>()
            .WithName("RegisterStockItem");

        group
            .MapPut("/{id:guid}", ([FromRoute] Guid id, UpdateStockItemRequest body, [FromServices] IStockItemService stock, CancellationToken cancellationToken) =>
                InventoryProblems.RunAsync(async () =>
                {
                    var item = await stock.UpdateAsync(
                        id,
                        new UpdateStockItemInput(
                            body.Category,
                            body.Name,
                            body.Unit,
                            body.UnitCost,
                            body.IsActive,
                            body.Description,
                            body.ManufacturerPartNumber,
                            body.StatusReason),
                        cancellationToken);

                    return Results.Ok(StockItemResponse.From(item));
                }))
            .RequirePermission(Permissions.Inventory.Write)
            .WithValidation<UpdateStockItemRequest>()
            .WithName("UpdateStockItem");

        // A movement is a thing that happened, not a field edit, so each one is its own POST
        // sub-resource per CONVENTIONS.md — and the answer is the ledger line it produced, which is
        // what tells the caller what is now on the shelf.
        group
            .MapPost("/{id:guid}/receipts", ([FromRoute] Guid id, ReceiveStockRequest body, [FromServices] IStockItemService stock, CancellationToken cancellationToken) =>
                InventoryProblems.RunAsync(async () =>
                    Results.Ok(StockMovementResponse.From(await stock.ReceiveAsync(
                        id,
                        new ReceiveStockInput(body.WarehouseId, body.Quantity, body.UnitCost, body.Reference, body.Note),
                        cancellationToken)))))
            .RequirePermission(Permissions.Inventory.Write)
            .WithValidation<ReceiveStockRequest>()
            .WithName("ReceiveStock");

        group
            .MapPost("/{id:guid}/issues", ([FromRoute] Guid id, IssueStockRequest body, [FromServices] IStockItemService stock, CancellationToken cancellationToken) =>
                InventoryProblems.RunAsync(async () =>
                    Results.Ok(StockMovementResponse.From(await stock.IssueAsync(
                        id,
                        new IssueStockInput(body.WarehouseId, body.Quantity, body.WorkOrderId, body.Reference, body.Note),
                        cancellationToken)))))
            .RequirePermission(Permissions.Inventory.Write)
            .WithValidation<IssueStockRequest>()
            .WithName("IssueStock");

        // The one endpoint in this module gated on something other than inventory.write. An
        // adjustment moves stock with nothing physically moving, which is why SPEC.md lists it
        // beside a bill adjustment and an approval as a sensitive action (invariant 5): a storeman
        // may receive and issue all day and still not be able to make a discrepancy disappear.
        group
            .MapPost("/{id:guid}/adjustments", ([FromRoute] Guid id, AdjustStockRequest body, [FromServices] IStockItemService stock, CancellationToken cancellationToken) =>
                InventoryProblems.RunAsync(async () =>
                    Results.Ok(StockMovementResponse.From(await stock.AdjustAsync(
                        id,
                        new AdjustStockInput(body.WarehouseId, body.CountedQuantity, body.Reason),
                        cancellationToken)))))
            .RequirePermission(Permissions.Inventory.Adjust)
            .WithValidation<AdjustStockRequest>()
            .WithName("AdjustStock");

        group
            .MapPost("/{id:guid}/minimum-quantity", ([FromRoute] Guid id, SetMinimumQuantityRequest body, [FromServices] IStockItemService stock, CancellationToken cancellationToken) =>
                InventoryProblems.RunAsync(async () =>
                    Results.Ok(StockItemResponse.From(await stock.SetMinimumQuantityAsync(
                        id,
                        new SetMinimumQuantityInput(body.WarehouseId, body.MinimumQuantity),
                        cancellationToken)))))
            .RequirePermission(Permissions.Inventory.Write)
            .WithValidation<SetMinimumQuantityRequest>()
            .WithName("SetStockMinimumQuantity");

        return endpoints;
    }
}
