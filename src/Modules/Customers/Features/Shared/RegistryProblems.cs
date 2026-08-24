using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GridCore.Modules.Customers.Features.Shared;

/// <summary>
/// Turns the registry failures into the RFC 7807 statuses CONVENTIONS.md prescribes: 400
/// validation, 404 missing, 409 workflow conflict. Shared by both slices so a customer and a
/// service location cannot drift into answering the same failure differently.
/// </summary>
public static class RegistryProblems
{
    /// <summary>Runs <paramref name="action"/>, translating any <see cref="RegistryException"/> it throws.</summary>
    public static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (CustomerNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Customer not found");
        }
        catch (ServiceLocationNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Service location not found");
        }
        catch (ServiceAccountNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Service account not found");
        }
        catch (RegistryWorkflowException exception)
        {
            return Problem(exception, StatusCodes.Status409Conflict, "The registry is not in that state");
        }
        catch (RegistryValidationException exception)
        {
            return Problem(exception, StatusCodes.Status400BadRequest, "The request is not valid");
        }
    }

    /// <summary>A 404 for an id that matched nothing on a read path, where nothing was thrown.</summary>
    public static IResult CustomerNotFound(Guid id) =>
        Problem(new CustomerNotFoundException(id), StatusCodes.Status404NotFound, "Customer not found");

    /// <summary>A 404 for a location id that matched nothing.</summary>
    public static IResult ServiceLocationNotFound(Guid id) =>
        Problem(new ServiceLocationNotFoundException(id), StatusCodes.Status404NotFound, "Service location not found");

    /// <summary>A 404 for an account id that matched nothing.</summary>
    public static IResult ServiceAccountNotFound(Guid id) =>
        Problem(new ServiceAccountNotFoundException(id), StatusCodes.Status404NotFound, "Service account not found");

    private static IResult Problem(RegistryException exception, int statusCode, string title) =>
        Results.Problem(new ProblemDetails
        {
            Title = title,
            Detail = exception.Message,
            Status = statusCode,
        });
}
