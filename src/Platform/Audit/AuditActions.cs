namespace GridCore.Platform.Audit;

/// <summary>
/// Canonical audit action names. Modules add their own constants here as they land, so the trail
/// can be filtered on a known vocabulary rather than free text. Naming:
/// <c>&lt;entity&gt;.&lt;verb&gt;</c>, lower case, dot separated.
/// </summary>
public static class AuditActions
{
    /// <summary>An approval request was raised.</summary>
    public const string ApprovalRequested = "approval.request";

    /// <summary>An approval request was approved.</summary>
    public const string ApprovalApproved = "approval.approve";

    /// <summary>An approval request was rejected.</summary>
    public const string ApprovalRejected = "approval.reject";

    /// <summary>An approval request was withdrawn by the person who raised it.</summary>
    public const string ApprovalCancelled = "approval.cancel";
}

/// <summary>Canonical audit entity-type names, prefixed with the owning module's schema.</summary>
public static class AuditEntityTypes
{
    /// <summary>A row of <c>platform.approval_requests</c>.</summary>
    public const string ApprovalRequest = "platform.approval_request";
}
