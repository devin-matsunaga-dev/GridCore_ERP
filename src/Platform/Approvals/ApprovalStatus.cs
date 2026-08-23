namespace GridCore.Platform.Approvals;

/// <summary>Where an approval request has got to. Persisted by name, so the numbers may move.</summary>
public enum ApprovalStatus
{
    /// <summary>Raised and awaiting a decision. The only state a decision may be made from.</summary>
    Pending = 0,

    /// <summary>Granted by someone other than the requester.</summary>
    Approved = 1,

    /// <summary>Refused by someone other than the requester.</summary>
    Rejected = 2,

    /// <summary>Withdrawn by the requester before anyone decided.</summary>
    Cancelled = 3,
}

/// <summary>
/// The approval state machine, as a pure function so the rules can be read and tested without a
/// database. Every terminal state is final: a decision is never revisited, a new request is raised
/// instead — the same principle as the append-only ledger.
/// </summary>
public static class ApprovalTransitions
{
    /// <summary>Whether <paramref name="from"/> may become <paramref name="to"/>.</summary>
    public static bool IsAllowed(ApprovalStatus from, ApprovalStatus to) =>
        from is ApprovalStatus.Pending && to is not ApprovalStatus.Pending && Enum.IsDefined(to);

    /// <summary>The states <paramref name="from"/> may move to, for a UI that renders allowed transitions as buttons.</summary>
    public static IReadOnlyList<ApprovalStatus> AllowedFrom(ApprovalStatus from) =>
        Enum.GetValues<ApprovalStatus>().Where(to => IsAllowed(from, to)).ToList();

    /// <summary>Whether <paramref name="status"/> is final.</summary>
    public static bool IsTerminal(ApprovalStatus status) => status is not ApprovalStatus.Pending;
}
