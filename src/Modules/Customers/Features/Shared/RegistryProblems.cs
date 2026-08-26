using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GridCore.Modules.Customers.Features.Shared;

/// <summary>
/// Turns the registry failures into the RFC 7807 statuses CONVENTIONS.md prescribes: 400
/// validation, 403 permission, 404 missing, 409 workflow conflict. Shared by every slice so a
/// customer and a service location cannot drift into answering the same failure differently.
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
        catch (CustomerContactNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Customer contact not found");
        }
        catch (CustomerNoteNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Customer note not found");
        }
        catch (ServiceLocationNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Service location not found");
        }
        catch (ServiceAccountNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Service account not found");
        }
        catch (ServiceApplicationNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Service application not found");
        }
        catch (ApplicationDocumentNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Application document not found");
        }
        catch (RegistryPermissionException exception)
        {
            return Problem(exception, StatusCodes.Status403Forbidden, "Not permitted");
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

    /// <summary>A 404 for a contact id that matched nothing on a read path, where nothing was thrown.</summary>
    public static IResult CustomerContactNotFound(Guid id) =>
        Problem(new CustomerContactNotFoundException(id), StatusCodes.Status404NotFound, "Customer contact not found");

    /// <summary>A 404 for a note id that matched nothing on a read path, where nothing was thrown.</summary>
    public static IResult CustomerNoteNotFound(Guid id) =>
        Problem(new CustomerNoteNotFoundException(id), StatusCodes.Status404NotFound, "Customer note not found");

    /// <summary>
    /// The 409 an attempt to edit a note earns.
    /// </summary>
    /// <remarks>
    /// A workflow conflict, not a validation failure: the request was perfectly well formed and the
    /// register is simply not one that works that way — the same distinction
    /// <c>CustomerDepositService</c> draws between a mistyped bill id and a bill that cannot take the
    /// money. The detail names the sub-resource that does what the caller wanted, because this is the
    /// rule of WP-2.13 a client is most likely to meet by trying it.
    /// </remarks>
    public static IResult NoteLogIsAppendOnly(Guid id) =>
        Problem(
            new RegistryWorkflowException(
                $"Note '{id}' cannot be edited: the customer note log is append-only, so what was written stays as it was written. "
                + $"POST /api/customer-notes/{id}/corrections to record a correction, which is a new note referencing this one."),
            StatusCodes.Status409Conflict,
            "The registry is not in that state");

    /// <summary>A 404 for a location id that matched nothing.</summary>
    public static IResult ServiceLocationNotFound(Guid id) =>
        Problem(new ServiceLocationNotFoundException(id), StatusCodes.Status404NotFound, "Service location not found");

    /// <summary>A 404 for an application id that matched nothing on a read path, where nothing was thrown.</summary>
    public static IResult ServiceApplicationNotFound(Guid id) =>
        Problem(new ServiceApplicationNotFoundException(id), StatusCodes.Status404NotFound, "Service application not found");

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
