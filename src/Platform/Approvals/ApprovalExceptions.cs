namespace GridCore.Platform.Approvals;

/// <summary>Base of the approval failures the endpoints translate into ProblemDetails responses.</summary>
public abstract class ApprovalException(string message) : Exception(message);

/// <summary>No approval request with that id. Surfaces as 404.</summary>
public sealed class ApprovalNotFoundException(Guid id)
    : ApprovalException($"Approval request '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid ApprovalRequestId { get; } = id;
}

/// <summary>The request is not in a state that allows what was asked. Surfaces as 409.</summary>
public sealed class ApprovalWorkflowException(string message) : ApprovalException(message);

/// <summary>The caller may not decide this request. Surfaces as 403.</summary>
public sealed class ApprovalPermissionException(string message) : ApprovalException(message);

/// <summary>The request as described could not be raised. Surfaces as 400.</summary>
public sealed class ApprovalValidationException(string message) : ApprovalException(message);
