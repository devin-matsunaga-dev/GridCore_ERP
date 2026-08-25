using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GridCore.Modules.Payments.Features.Shared;

/// <summary>
/// Turns the payments register's failures into the RFC 7807 statuses CONVENTIONS.md prescribes: 400
/// validation, 404 missing, 409 workflow conflict.
/// </summary>
public static class PaymentProblems
{
    /// <summary>Runs <paramref name="action"/>, translating any <see cref="PaymentRegistryException"/> it throws.</summary>
    public static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (PaymentNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Payment not found");
        }
        catch (BillNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Bill not found");
        }
        catch (ServiceAccountNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Service account not found");
        }
        catch (PaymentWorkflowException exception)
        {
            return Problem(exception, StatusCodes.Status409Conflict, "The payments register is not in that state");
        }
        catch (PaymentValidationException exception)
        {
            return Problem(exception, StatusCodes.Status400BadRequest, "The request is not valid");
        }
    }

    /// <summary>A 404 for an id that matched nothing on a read path, where nothing was thrown.</summary>
    public static IResult PaymentNotFound(Guid id) =>
        Problem(new PaymentNotFoundException(id), StatusCodes.Status404NotFound, "Payment not found");

    private static IResult Problem(PaymentRegistryException exception, int statusCode, string title) =>
        Results.Problem(new ProblemDetails
        {
            Title = title,
            Detail = exception.Message,
            Status = statusCode,
        });
}
