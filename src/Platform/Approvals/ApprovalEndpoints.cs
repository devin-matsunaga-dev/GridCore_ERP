using GridCore.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Platform.Approvals;

/// <summary>Body of a request to raise an approval.</summary>
/// <param name="RequestType">The kind of decision, e.g. <c>billing.adjustment</c>.</param>
/// <param name="SubjectType">The kind of entity being decided about.</param>
/// <param name="SubjectId">Identifier of that entity.</param>
/// <param name="RequiredPermission">Permission a decider must hold.</param>
/// <param name="Reason">Why it is being asked for.</param>
/// <param name="Payload">Free-form detail for the approver.</param>
public sealed record RaiseApprovalRequest(
    string RequestType,
    string SubjectType,
    string SubjectId,
    string RequiredPermission,
    string? Reason = null,
    Dictionary<string, string>? Payload = null);

/// <summary>Body of a decision on an approval.</summary>
/// <param name="Note">What the decider wants recorded.</param>
public sealed record ApprovalDecisionRequest(string? Note = null);

/// <summary>An approval request as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="RequestType">The kind of decision.</param>
/// <param name="SubjectType">Kind of entity decided about.</param>
/// <param name="SubjectId">Identifier of that entity.</param>
/// <param name="RequiredPermission">Permission a decider must hold.</param>
/// <param name="Reason">Why it was raised.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="AllowedTransitions">States it may still move to — what a UI renders as buttons.</param>
/// <param name="RequestedByUserId">Who raised it.</param>
/// <param name="RequestedByUserName">Their display name.</param>
/// <param name="RequestedAt">When.</param>
/// <param name="DecidedByUserName">Who decided, once decided.</param>
/// <param name="DecidedAt">When they decided.</param>
/// <param name="DecisionNote">What they said.</param>
public sealed record ApprovalResponse(
    Guid Id,
    string RequestType,
    string SubjectType,
    string SubjectId,
    string RequiredPermission,
    string? Reason,
    string Status,
    IReadOnlyList<string> AllowedTransitions,
    string RequestedByUserId,
    string? RequestedByUserName,
    DateTimeOffset RequestedAt,
    string? DecidedByUserName,
    DateTimeOffset? DecidedAt,
    string? DecisionNote)
{
    /// <summary>Projects an <see cref="ApprovalRequest"/> for the wire.</summary>
    public static ApprovalResponse From(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ApprovalResponse(
            request.Id,
            request.RequestType,
            request.SubjectType,
            request.SubjectId,
            request.RequiredPermission,
            request.Reason,
            request.Status.ToString(),
            request.AllowedTransitions.Select(status => status.ToString()).ToList(),
            request.RequestedByUserId,
            request.RequestedByUserName,
            request.RequestedAt,
            request.DecidedByUserName,
            request.DecidedAt,
            request.DecisionNote);
    }
}

/// <summary>The approval queue's HTTP surface. Mapped by the host alongside the modules.</summary>
public static class ApprovalEndpoints
{
    /// <summary>Route prefix of the approval queue.</summary>
    public const string RoutePrefix = "/api/approvals";

    /// <summary>Maps the approval endpoints.</summary>
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Approvals");

        // Raising a request is not itself sensitive — anyone signed in may ask. Deciding is what is
        // gated, both on platform.approve below and on the request's own required permission.
        group
            .MapPost("/", async (RaiseApprovalRequest body, [FromServices] IApprovalService approvals, CancellationToken cancellationToken) =>
                await RunAsync(
                    async () =>
                    {
                        var request = await approvals.RequestAsync(
                            new ApprovalRequestInput(
                                body.RequestType,
                                body.SubjectType,
                                body.SubjectId,
                                body.RequiredPermission,
                                body.Payload,
                                body.Reason),
                            cancellationToken);

                        return Results.Created($"{RoutePrefix}/{request.Id}", ApprovalResponse.From(request));
                    }))
            .RequireAuthorization()
            .WithName("RaiseApproval");

        group
            .MapGet("/", async (
                    ApprovalStatus? status,
                    int? limit,
                    [FromServices] IApprovalService approvals,
                    CancellationToken cancellationToken) =>
                Results.Ok((await approvals.ListAsync(status, limit ?? 50, cancellationToken))
                    .Select(ApprovalResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Platform.Approve)
            .WithName("ListApprovals");

        group
            .MapGet("/{id:guid}", async (Guid id, [FromServices] IApprovalService approvals, CancellationToken cancellationToken) =>
            {
                var request = await approvals.FindAsync(id, cancellationToken);

                return request is null ? NotFound(id) : Results.Ok(ApprovalResponse.From(request));
            })
            .RequireAuthorization()
            .WithName("GetApproval");

        group
            .MapPost("/{id:guid}/approve", (Guid id, ApprovalDecisionRequest? body, [FromServices] IApprovalService approvals, CancellationToken cancellationToken) =>
                DecideAsync(() => approvals.ApproveAsync(id, body?.Note, cancellationToken)))
            .RequirePermission(Permissions.Platform.Approve)
            .WithName("ApproveApproval");

        group
            .MapPost("/{id:guid}/reject", (Guid id, ApprovalDecisionRequest? body, [FromServices] IApprovalService approvals, CancellationToken cancellationToken) =>
                DecideAsync(() => approvals.RejectAsync(id, body?.Note, cancellationToken)))
            .RequirePermission(Permissions.Platform.Approve)
            .WithName("RejectApproval");

        // Withdrawing is the requester's own right, so it is authenticated rather than gated; the
        // service refuses anyone else.
        group
            .MapPost("/{id:guid}/cancel", (Guid id, ApprovalDecisionRequest? body, [FromServices] IApprovalService approvals, CancellationToken cancellationToken) =>
                DecideAsync(() => approvals.CancelAsync(id, body?.Note, cancellationToken)))
            .RequireAuthorization()
            .WithName("CancelApproval");

        return endpoints;
    }

    private static Task<IResult> DecideAsync(Func<Task<ApprovalRequest>> decide) =>
        RunAsync(async () => Results.Ok(ApprovalResponse.From(await decide())));

    /// <summary>
    /// Turns the approval failures into the RFC 7807 statuses CONVENTIONS.md prescribes: 400
    /// validation, 403 permission, 404 missing, 409 workflow conflict.
    /// </summary>
    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ApprovalNotFoundException exception)
        {
            return Problem(exception, StatusCodes.Status404NotFound, "Approval request not found");
        }
        catch (ApprovalPermissionException exception)
        {
            return Problem(exception, StatusCodes.Status403Forbidden, "Not permitted to decide this request");
        }
        catch (ApprovalWorkflowException exception)
        {
            return Problem(exception, StatusCodes.Status409Conflict, "Approval request is not in that state");
        }
        catch (ApprovalValidationException exception)
        {
            return Problem(exception, StatusCodes.Status400BadRequest, "Approval request is incomplete");
        }
    }

    private static IResult NotFound(Guid id) =>
        Problem(new ApprovalNotFoundException(id), StatusCodes.Status404NotFound, "Approval request not found");

    private static IResult Problem(ApprovalException exception, int statusCode, string title) =>
        Results.Problem(new ProblemDetails
        {
            Title = title,
            Detail = exception.Message,
            Status = statusCode,
        });
}
