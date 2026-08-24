using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GridCore.Modules.Metering.Features.Shared;

/// <summary>
/// Turns the meter register's failures into the RFC 7807 statuses CONVENTIONS.md prescribes: 400
/// validation, 404 missing, 409 workflow conflict.
/// </summary>
public static class MeterProblems
{
    /// <summary>Runs <paramref name="action"/>, translating any <see cref="MeterRegistryException"/> it throws.</summary>
    public static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (MeterNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Meter not found");
        }
        catch (ServiceLocationNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Service location not found");
        }
        catch (MeterWorkflowException exception)
        {
            return Problem(exception, StatusCodes.Status409Conflict, "The meter register is not in that state");
        }
        catch (MeterValidationException exception)
        {
            return Problem(exception, StatusCodes.Status400BadRequest, "The request is not valid");
        }
    }

    /// <summary>A 404 for an id that matched nothing on a read path, where nothing was thrown.</summary>
    public static IResult MeterNotFound(Guid id) =>
        Problem(new MeterNotFoundException(id), StatusCodes.Status404NotFound, "Meter not found");

    private static IResult Problem(MeterRegistryException exception, int statusCode, string title) =>
        Results.Problem(new ProblemDetails
        {
            Title = title,
            Detail = exception.Message,
            Status = statusCode,
        });
}
