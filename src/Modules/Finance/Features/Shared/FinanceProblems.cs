using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GridCore.Modules.Finance.Features.Shared;

/// <summary>
/// Turns the ledger's failures into the RFC 7807 statuses CONVENTIONS.md prescribes: 400
/// validation, 404 missing.
/// </summary>
/// <remarks>
/// There is no 409 here, unlike every registry before it. Finance's HTTP surface is read-only in
/// this work package — a ledger has no state a caller can put it in the wrong one of, because the
/// only thing that writes to it is a consumer reacting to a fact that has already happened.
/// </remarks>
public static class FinanceProblems
{
    /// <summary>Runs <paramref name="action"/>, translating any <see cref="FinanceException"/> it throws.</summary>
    public static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (JournalEntryNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Journal entry not found");
        }
        catch (FinanceValidationException exception)
        {
            return Problem(exception, StatusCodes.Status400BadRequest, "The request is not valid");
        }
    }

    /// <summary>A 404 for an id that matched nothing on a read path, where nothing was thrown.</summary>
    public static IResult JournalEntryNotFound(Guid id) =>
        Problem(new JournalEntryNotFoundException(id), StatusCodes.Status404NotFound, "Journal entry not found");

    private static IResult Problem(FinanceException exception, int statusCode, string title) =>
        Results.Problem(new ProblemDetails
        {
            Title = title,
            Detail = exception.Message,
            Status = statusCode,
        });
}
