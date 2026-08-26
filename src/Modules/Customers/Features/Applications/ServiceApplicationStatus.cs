namespace GridCore.Modules.Customers.Features.Applications;

/// <summary>
/// Where a service application stands. This is the <i>application's</i> status, not the account's:
/// an approved application has an account behind it and that account has a lifecycle of its own
/// (WP-1.2), which starts at <see cref="ServiceAccounts.ServiceAccountStatus.Pending"/> and knows
/// nothing about the form that produced it.
/// </summary>
public enum ServiceApplicationStatus
{
    /// <summary>Filed and waiting to be picked up. Where every application starts.</summary>
    Submitted = 1,

    /// <summary>A reviewer has it. Documents may still be attached, and this is the only status a decision may be taken from.</summary>
    UnderReview = 2,

    /// <summary>
    /// Accepted, and the service account is open. Terminal: what was approved stays approved, and a
    /// customer who wants a second supply applies again.
    /// </summary>
    Approved = 3,

    /// <summary>
    /// Refused, with a reason code saying why. Terminal — a rejected application is <b>not</b>
    /// reopened, because the thing that changed is the applicant's evidence and that is a fresh
    /// submission.
    /// </summary>
    Rejected = 4,

    /// <summary>The applicant took it back, or the desk could not reach them. Terminal, for the reason a rejection is.</summary>
    Withdrawn = 5,
}

/// <summary>
/// The service application state machine, in one place. Kept out of
/// <see cref="ServiceApplication"/> so a UI can ask what is legal without holding an entity —
/// the call <c>CustomerTransitions</c> and <c>ServiceAccountTransitions</c> already made.
/// </summary>
public static class ServiceApplicationTransitions
{
    private static readonly Dictionary<ServiceApplicationStatus, ServiceApplicationStatus[]> Allowed = new()
    {
        // No Submitted -> Approved and no Submitted -> Rejected. "CUC reviews an application before
        // it establishes an account" is the whole of WORK_PACKAGES.md WP-2.18, and a state machine
        // that let a decision be taken off the queue without the application ever being picked up
        // would make the review step a convention rather than a rule. Picking it up costs one call.
        [ServiceApplicationStatus.Submitted] = [ServiceApplicationStatus.UnderReview, ServiceApplicationStatus.Withdrawn],

        [ServiceApplicationStatus.UnderReview] =
        [
            ServiceApplicationStatus.Approved,
            ServiceApplicationStatus.Rejected,
            ServiceApplicationStatus.Withdrawn,
        ],

        // All three terminal. A decision that turned out to be wrong is a fresh application naming
        // the one it replaces — the same call WP-2.13's note log and WP-2.15's transition register
        // make, and the reason ServiceApplication.Replaces exists.
        [ServiceApplicationStatus.Approved] = [],
        [ServiceApplicationStatus.Rejected] = [],
        [ServiceApplicationStatus.Withdrawn] = [],
    };

    /// <summary>The statuses an application in <paramref name="status"/> may move to.</summary>
    public static IReadOnlyList<ServiceApplicationStatus> AllowedFrom(ServiceApplicationStatus status) =>
        Allowed.TryGetValue(status, out var next) ? next : [];

    /// <summary>Whether <paramref name="from"/> → <paramref name="to"/> is a legal move.</summary>
    public static bool IsAllowed(ServiceApplicationStatus from, ServiceApplicationStatus to) =>
        AllowedFrom(from).Contains(to);

    /// <summary>Whether an application in <paramref name="status"/> has been decided and will not move again.</summary>
    public static bool IsTerminal(ServiceApplicationStatus status) => AllowedFrom(status).Count is 0;

    /// <summary>
    /// Whether an application in <paramref name="status"/> is still on the desk — the filter the
    /// review queue is drawn from, and the statuses a document may still be attached under.
    /// </summary>
    public static bool IsOpen(ServiceApplicationStatus status) => !IsTerminal(status);
}
