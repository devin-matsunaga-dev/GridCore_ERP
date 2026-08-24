using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GridCore.Modules.Assets.Features.Shared;

/// <summary>
/// Turns the asset register's failures into the RFC 7807 statuses CONVENTIONS.md prescribes: 400
/// validation, 404 missing, 409 workflow conflict.
/// </summary>
public static class AssetProblems
{
    /// <summary>Runs <paramref name="action"/>, translating any <see cref="AssetRegistryException"/> it throws.</summary>
    public static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (AssetNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Asset not found");
        }
        catch (AssetWorkflowException exception)
        {
            return Problem(exception, StatusCodes.Status409Conflict, "The asset register is not in that state");
        }
        catch (AssetValidationException exception)
        {
            return Problem(exception, StatusCodes.Status400BadRequest, "The request is not valid");
        }
    }

    /// <summary>A 404 for an id that matched nothing on a read path, where nothing was thrown.</summary>
    public static IResult AssetNotFound(Guid id) =>
        Problem(new AssetNotFoundException(id), StatusCodes.Status404NotFound, "Asset not found");

    private static IResult Problem(AssetRegistryException exception, int statusCode, string title) =>
        Results.Problem(new ProblemDetails
        {
            Title = title,
            Detail = exception.Message,
            Status = statusCode,
        });
}
