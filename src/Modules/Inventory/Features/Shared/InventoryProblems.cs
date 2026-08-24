using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GridCore.Modules.Inventory.Features.Shared;

/// <summary>
/// Turns the inventory module's failures into the RFC 7807 statuses CONVENTIONS.md prescribes: 400
/// validation, 404 missing, 409 workflow conflict.
/// </summary>
public static class InventoryProblems
{
    /// <summary>Runs <paramref name="action"/>, translating any <see cref="InventoryException"/> it throws.</summary>
    public static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (StockItemNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Stock item not found");
        }
        catch (WarehouseNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Warehouse not found");
        }
        catch (InventoryWorkflowException exception)
        {
            return Problem(exception, StatusCodes.Status409Conflict, "The stock is not in that state");
        }
        catch (InventoryValidationException exception)
        {
            return Problem(exception, StatusCodes.Status400BadRequest, "The request is not valid");
        }
    }

    /// <summary>A 404 for an id that matched nothing on a read path, where nothing was thrown.</summary>
    public static IResult StockItemNotFound(Guid id) =>
        Problem(new StockItemNotFoundException(id), StatusCodes.Status404NotFound, "Stock item not found");

    private static IResult Problem(InventoryException exception, int statusCode, string title) =>
        Results.Problem(new ProblemDetails
        {
            Title = title,
            Detail = exception.Message,
            Status = statusCode,
        });
}
