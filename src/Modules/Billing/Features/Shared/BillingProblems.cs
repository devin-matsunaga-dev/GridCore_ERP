using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GridCore.Modules.Billing.Features.Shared;

/// <summary>
/// Turns the billing register's failures into the RFC 7807 statuses CONVENTIONS.md prescribes: 400
/// validation, 404 missing, 409 workflow conflict.
/// </summary>
public static class BillingProblems
{
    /// <summary>Runs <paramref name="action"/>, translating any <see cref="BillingRegistryException"/> it throws.</summary>
    public static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (BillNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Bill not found");
        }
        catch (AccountChargeNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Account charge not found");
        }
        catch (RatePlanNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Rate plan not found");
        }
        catch (ServiceAccountNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Service account not found");
        }
        catch (BillingPermissionException exception)
        {
            return Problem(exception, StatusCodes.Status403Forbidden, "Not permitted");
        }
        catch (BillingWorkflowException exception)
        {
            return Problem(exception, StatusCodes.Status409Conflict, "The billing register is not in that state");
        }
        catch (BillingValidationException exception)
        {
            return Problem(exception, StatusCodes.Status400BadRequest, "The request is not valid");
        }
    }

    /// <summary>A 404 for an id that matched nothing on a read path, where nothing was thrown.</summary>
    public static IResult BillNotFound(Guid id) =>
        Problem(new BillNotFoundException(id), StatusCodes.Status404NotFound, "Bill not found");

    /// <summary>A 404 for a charge id that matched nothing on a read path.</summary>
    public static IResult AccountChargeNotFound(Guid id) =>
        Problem(new AccountChargeNotFoundException(id), StatusCodes.Status404NotFound, "Account charge not found");

    private static IResult Problem(BillingRegistryException exception, int statusCode, string title) =>
        Results.Problem(new ProblemDetails
        {
            Title = title,
            Detail = exception.Message,
            Status = statusCode,
        });
}
