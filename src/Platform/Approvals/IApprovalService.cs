namespace GridCore.Platform.Approvals;

/// <summary>What a module needs to say to raise an approval request.</summary>
/// <param name="RequestType">The kind of decision, e.g. <c>billing.adjustment</c>.</param>
/// <param name="SubjectType">The kind of entity being decided about, e.g. <c>billing.bill</c>.</param>
/// <param name="SubjectId">Identifier of that entity.</param>
/// <param name="RequiredPermission">The permission a decider must hold — see <see cref="Security.Permissions"/>.</param>
/// <param name="Payload">What is being asked for, serialised into the request for the approver to read.</param>
/// <param name="Reason">Why it is being asked for.</param>
public sealed record ApprovalRequestInput(
    string RequestType,
    string SubjectType,
    string SubjectId,
    string RequiredPermission,
    object? Payload = null,
    string? Reason = null);

/// <summary>
/// The reusable approval workflow: request, then approve or reject. Every call is audited, and the
/// permission checks live here rather than at the edge, so a module calling in-process is gated
/// exactly like an HTTP caller.
/// </summary>
public interface IApprovalService
{
    /// <summary>Raises a pending request on behalf of the current user.</summary>
    /// <exception cref="ApprovalValidationException">The request is incomplete or names an unknown permission.</exception>
    Task<ApprovalRequest> RequestAsync(ApprovalRequestInput input, CancellationToken cancellationToken = default);

    /// <summary>Approves a pending request.</summary>
    /// <exception cref="ApprovalNotFoundException">No such request.</exception>
    /// <exception cref="ApprovalPermissionException">The caller does not hold the required permission.</exception>
    /// <exception cref="ApprovalWorkflowException">Already decided, or decided by its own requester.</exception>
    Task<ApprovalRequest> ApproveAsync(Guid id, string? note = null, CancellationToken cancellationToken = default);

    /// <summary>Rejects a pending request. Same rules as <see cref="ApproveAsync"/>.</summary>
    Task<ApprovalRequest> RejectAsync(Guid id, string? note = null, CancellationToken cancellationToken = default);

    /// <summary>Withdraws a pending request. Only the requester may.</summary>
    /// <exception cref="ApprovalNotFoundException">No such request.</exception>
    /// <exception cref="ApprovalWorkflowException">Already decided, or the caller did not raise it.</exception>
    Task<ApprovalRequest> CancelAsync(Guid id, string? note = null, CancellationToken cancellationToken = default);

    /// <summary>One request by id, or <see langword="null"/>.</summary>
    Task<ApprovalRequest?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The queue, newest first, optionally filtered to one status.</summary>
    Task<IReadOnlyList<ApprovalRequest>> ListAsync(
        ApprovalStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default);
}
