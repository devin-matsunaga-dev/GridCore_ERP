using GridCore.Modules.Inventory.Data;
using GridCore.Modules.Inventory.Features.Items;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Inventory.Features.Warehouses;

/// <summary>A warehouse and what it is holding, as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="Code">The code a person quotes.</param>
/// <param name="Name">What the warehouse is called.</param>
/// <param name="Location">Where it is.</param>
/// <param name="IsActive">Whether stock may still move through it.</param>
/// <param name="LinesHeld">How many catalogue lines it holds stock of.</param>
/// <param name="LinesBelowMinimum">How many of those are at or below their reorder level.</param>
public sealed record WarehouseResponse(
    Guid Id,
    string Code,
    string Name,
    string? Location,
    bool IsActive,
    int LinesHeld,
    int LinesBelowMinimum);

/// <summary>
/// The warehouse list. Read-only on purpose: warehouses are reference data shipped by migration
/// (WP-0.8), so there is no endpoint that creates, edits or closes one — adding a warehouse is a
/// migration, which is what keeps every deployment holding the same set (invariants 7 and 8).
/// </summary>
public interface IWarehouseService
{
    /// <summary>Every warehouse, in code order, with a summary of what it holds.</summary>
    Task<IReadOnlyList<WarehouseResponse>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>The warehouse list over the inventory schema.</summary>
public sealed class WarehouseService(InventoryDbContext database) : IWarehouseService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<WarehouseResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var warehouses = await database.Warehouses
            .AsNoTracking()
            .OrderBy(warehouse => warehouse.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Grouped queries rather than a correlated subquery per row, and two of them rather than one
        // conditional count: that way the low-stock rule stays the single expression the catalogue
        // list also uses (StockLevel.BelowMinimum) instead of a second copy written in SQL here,
        // which is exactly the drift StockLevel documents.
        var linesHeld = await CountByWarehouseAsync(database.StockLevels.AsNoTracking(), cancellationToken)
            .ConfigureAwait(false);

        var linesLow = await CountByWarehouseAsync(
            database.StockLevels.AsNoTracking().Where(StockLevel.BelowMinimum),
            cancellationToken).ConfigureAwait(false);

        return warehouses
            .Select(warehouse => new WarehouseResponse(
                warehouse.Id,
                warehouse.Code,
                warehouse.Name,
                warehouse.Location,
                warehouse.IsActive,
                linesHeld.GetValueOrDefault(warehouse.Id),
                linesLow.GetValueOrDefault(warehouse.Id)))
            .ToList();
    }

    private static async Task<Dictionary<Guid, int>> CountByWarehouseAsync(
        IQueryable<StockLevel> levels,
        CancellationToken cancellationToken) =>
        await levels
            .GroupBy(level => level.WarehouseId)
            .Select(group => new { WarehouseId = group.Key, Lines = group.Count() })
            .ToDictionaryAsync(summary => summary.WarehouseId, summary => summary.Lines, cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>The warehouse list's HTTP surface.</summary>
public static class WarehouseEndpoints
{
    /// <summary>Route prefix of the warehouse list.</summary>
    public const string RoutePrefix = "/api/inventory/warehouses";

    /// <summary>Maps the warehouse endpoints.</summary>
    public static IEndpointRouteBuilder MapWarehouseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet(RoutePrefix, async ([FromServices] IWarehouseService warehouses, CancellationToken cancellationToken) =>
                Results.Ok(await warehouses.ListAsync(cancellationToken)))
            .RequirePermission(Permissions.Inventory.Read)
            .WithTags("Inventory")
            .WithName("ListWarehouses");

        return endpoints;
    }
}
