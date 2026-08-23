using GridCore.Platform.Audit;
using GridCore.Platform.Security;

namespace GridCore.Platform.Approvals;

/// <summary>
/// A reusable "someone must say yes before this happens" record. Modules raise one instead of
/// building their own approval table: a bill adjustment, a purchase order and an inventory
/// correction differ only in <see cref="RequestType"/>, <see cref="SubjectType"/> and
/// <see cref="RequiredPermission"/>.
/// </summary>
public sealed class ApprovalRequest
{
    /// <summary>Longest value a name-like column stores.</summary>
    public const int NameLength = 256;

    /// <summary>Longest free-text reason or decision note stored.</summary>
    public const int NoteLength = 1024;

    private ApprovalRequest()
    {
        // EF materialisation.
        RequestType = string.Empty;
        SubjectType = string.Empty;
        SubjectId = string.Empty;
        RequiredPermission = string.Empty;
        RequestedByUserId = string.Empty;
    }

    /// <summary>Identifier of this request. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>What kind of decision this is, e.g. <c>billing.adjustment</c>. Owned by the raising module.</summary>
    public string RequestType { get; private init; }

    /// <summary>The kind of entity the decision is about, e.g. <c>billing.bill</c>.</summary>
    public string SubjectType { get; private init; }

    /// <summary>Identifier of the entity the decision is about.</summary>
    public string SubjectId { get; private init; }

    /// <summary>
    /// The permission a decider must hold on top of <see cref="Permissions.Platform.Approve"/>.
    /// This is what keeps the primitive reusable: a purchase order needs
    /// <see cref="Permissions.Purchasing.Approve"/>, a bill adjustment needs
    /// <see cref="Permissions.Billing.Adjust"/>, and neither needs its own approval table.
    /// </summary>
    public string RequiredPermission { get; private init; }

    /// <summary>JSON description of what is being asked for, for the approver to read.</summary>
    public string? PayloadJson { get; private init; }

    /// <summary>Why it was raised.</summary>
    public string? Reason { get; private init; }

    /// <summary>Subject id of whoever raised it.</summary>
    public string RequestedByUserId { get; private init; }

    /// <summary>Display name of whoever raised it.</summary>
    public string? RequestedByUserName { get; private init; }

    /// <summary>When it was raised.</summary>
    public DateTimeOffset RequestedAt { get; private init; }

    /// <summary>Where it has got to.</summary>
    public ApprovalStatus Status { get; private set; }

    /// <summary>Subject id of whoever decided, once decided.</summary>
    public string? DecidedByUserId { get; private set; }

    /// <summary>Display name of whoever decided, once decided.</summary>
    public string? DecidedByUserName { get; private set; }

    /// <summary>When it was decided.</summary>
    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>What the decider said.</summary>
    public string? DecisionNote { get; private set; }

    /// <summary>The transitions available from the current state, for rendering decision buttons.</summary>
    public IReadOnlyList<ApprovalStatus> AllowedTransitions => ApprovalTransitions.AllowedFrom(Status);

    /// <summary>Raises a pending request on behalf of <paramref name="requester"/>.</summary>
    /// <exception cref="ApprovalValidationException">A required field is missing, or the permission is not one GridCore declares.</exception>
    public static ApprovalRequest Raise(
        string requestType,
        string subjectType,
        string subjectId,
        string requiredPermission,
        object? payload,
        string? reason,
        ICurrentUser requester,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(requester);

        Require(requestType, nameof(requestType));
        Require(subjectType, nameof(subjectType));
        Require(subjectId, nameof(subjectId));
        Require(requiredPermission, nameof(requiredPermission));

        // An unknown permission would produce a request nobody could ever decide, so it is a
        // validation failure at the door rather than a stuck row.
        if (!Permissions.All.Contains(requiredPermission))
        {
            throw new ApprovalValidationException(
                $"'{requiredPermission}' is not a permission GridCore declares, so no one could decide this request.");
        }

        return new ApprovalRequest
        {
            Id = Guid.CreateVersion7(now),
            RequestType = Trim(requestType, NameLength)!,
            SubjectType = Trim(subjectType, NameLength)!,
            SubjectId = Trim(subjectId, NameLength)!,
            RequiredPermission = requiredPermission,
            PayloadJson = AuditEntry.Snapshot(payload),
            Reason = Trim(reason, NoteLength),
            RequestedByUserId = Trim(requester.UserId, NameLength)!,
            RequestedByUserName = Trim(requester.UserName, NameLength),
            RequestedAt = now,
            Status = ApprovalStatus.Pending,
        };
    }

    /// <summary>Approves the request.</summary>
    /// <exception cref="ApprovalWorkflowException">Already decided, or decided by its own requester.</exception>
    public void Approve(ICurrentUser decider, string? note, DateTimeOffset now) =>
        Decide(ApprovalStatus.Approved, decider, note, now);

    /// <summary>Rejects the request.</summary>
    /// <exception cref="ApprovalWorkflowException">Already decided, or decided by its own requester.</exception>
    public void Reject(ICurrentUser decider, string? note, DateTimeOffset now) =>
        Decide(ApprovalStatus.Rejected, decider, note, now);

    /// <summary>Withdraws the request. Only the requester may do this.</summary>
    /// <exception cref="ApprovalWorkflowException">Already decided, or cancelled by someone else.</exception>
    public void Cancel(ICurrentUser actor, string? note, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!string.Equals(actor.UserId, RequestedByUserId, StringComparison.Ordinal))
        {
            throw new ApprovalWorkflowException(
                "Only the person who raised an approval request may withdraw it; anyone else rejects it.");
        }

        Transition(ApprovalStatus.Cancelled, actor, note, now);
    }

    private void Decide(ApprovalStatus outcome, ICurrentUser decider, string? note, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decider);

        // Separation of duties: the point of the primitive is that a second person looks at it.
        if (string.Equals(decider.UserId, RequestedByUserId, StringComparison.Ordinal))
        {
            throw new ApprovalWorkflowException(
                "An approval request cannot be decided by the person who raised it.");
        }

        Transition(outcome, decider, note, now);
    }

    private void Transition(ApprovalStatus to, ICurrentUser actor, string? note, DateTimeOffset now)
    {
        if (!ApprovalTransitions.IsAllowed(Status, to))
        {
            throw new ApprovalWorkflowException(
                $"An approval request cannot go from {Status} to {to}; it was already {Status}.");
        }

        Status = to;
        DecidedByUserId = Trim(actor.UserId, NameLength);
        DecidedByUserName = Trim(actor.UserName, NameLength);
        DecidedAt = now;
        DecisionNote = Trim(note, NoteLength);
    }

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ApprovalValidationException($"'{field}' is required to raise an approval request.");
        }
    }

    private static string? Trim(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
