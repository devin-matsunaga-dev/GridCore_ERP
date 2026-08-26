namespace GridCore.Modules.Customers.Features.Applications;

/// <summary>
/// Why an application ended where it did — the fixed list WORK_PACKAGES.md WP-2.18 asks for on
/// "every terminal move".
/// </summary>
/// <remarks>
/// <para>
/// <b>One list, with <see cref="ApplicationReasons"/> saying which codes fit which decision.</b> The
/// shape <c>TransitionReasonCode</c> takes, and for the same reason: a code per status would spell
/// "the applicant asked us to stop" twice and leave a report adding the two spellings together.
/// </para>
/// <para>
/// <b><see cref="Other"/> is the escape hatch and it is the one code that must explain itself.</b> A
/// fixed list without one is defeated by picking the nearest wrong code; one whose escape hatch may
/// be silent is fixed in name only. See <see cref="ApplicationReasons.RequiresNotes"/>.
/// </para>
/// </remarks>
public enum ApplicationReasonCode
{
    /// <summary>None of the codes below fits. Free text is required with it, and only with it.</summary>
    Other,

    /// <summary>The checklist was satisfied and the evidence was accepted. The ordinary approval.</summary>
    DocumentsVerified,

    /// <summary>An officer accepted it outside the ordinary evidence — a CUC employee, a government premise, a rebuild after a storm.</summary>
    ApprovedByException,

    /// <summary>Something the checklist asks for was never produced. The ordinary rejection.</summary>
    DocumentsIncomplete,

    /// <summary>The utility is not satisfied the applicant is who they say they are.</summary>
    IdentityNotVerified,

    /// <summary>The applicant could not show they are entitled to take service at the premise.</summary>
    OccupancyNotProven,

    /// <summary>The premise cannot be supplied as it stands — no line, no meter position, condemned.</summary>
    PremiseNotServiceable,

    /// <summary>The applicant already owes the utility money, on this account or another.</summary>
    OutstandingBalance,

    /// <summary>The same application has already been filed. What a desk finds when two reps take one telephone call.</summary>
    DuplicateApplication,

    /// <summary>The applicant asked for it to be taken back.</summary>
    ApplicantWithdrew,

    /// <summary>The desk could not reach the applicant to finish it. The reason a queue does not grow forever.</summary>
    ApplicantUnreachable,

    /// <summary>The supply was taken up under somebody else's application — a landlord's, a spouse's.</summary>
    SupersededByAnotherApplication,
}

/// <summary>
/// Which reason codes are legal against which decision, and which of them has to say more.
/// </summary>
/// <remarks>
/// Pure and static, so a UI can render the right select without holding an entity — the call
/// <c>TransitionReasons</c> already made, and the reason the browser's approve and reject dialogs
/// read the same list the aggregate enforces.
/// </remarks>
public static class ApplicationReasons
{
    private static readonly Dictionary<ServiceApplicationStatus, ApplicationReasonCode[]> Allowed = new()
    {
        // Short on purpose. An approval is either "the evidence was there" or "somebody decided to
        // proceed anyway", and the second one should be uncomfortable to record — it is the entry an
        // auditor goes looking for.
        [ServiceApplicationStatus.Approved] =
        [
            ApplicationReasonCode.DocumentsVerified,
            ApplicationReasonCode.ApprovedByException,
            ApplicationReasonCode.Other,
        ],

        [ServiceApplicationStatus.Rejected] =
        [
            ApplicationReasonCode.DocumentsIncomplete,
            ApplicationReasonCode.IdentityNotVerified,
            ApplicationReasonCode.OccupancyNotProven,
            ApplicationReasonCode.PremiseNotServiceable,
            ApplicationReasonCode.OutstandingBalance,
            ApplicationReasonCode.DuplicateApplication,
            ApplicationReasonCode.Other,
        ],

        // No DocumentsIncomplete here, deliberately: an application the utility refused because the
        // evidence was missing is a REJECTION, and letting a desk file it as a withdrawal instead
        // would move the utility's own decision onto the applicant's record.
        [ServiceApplicationStatus.Withdrawn] =
        [
            ApplicationReasonCode.ApplicantWithdrew,
            ApplicationReasonCode.ApplicantUnreachable,
            ApplicationReasonCode.SupersededByAnotherApplication,
            ApplicationReasonCode.Other,
        ],
    };

    /// <summary>The reason codes a decision of <paramref name="status"/> may be recorded under.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The status is not a terminal one. Not an empty list: asking this of
    /// <see cref="ServiceApplicationStatus.Submitted"/> is a caller confusing a decision with a
    /// hand-off, and an empty answer would let them record a reason against neither.
    /// </exception>
    public static IReadOnlyList<ApplicationReasonCode> For(ServiceApplicationStatus status) =>
        Allowed.TryGetValue(status, out var codes)
            ? codes
            : throw new ArgumentOutOfRangeException(nameof(status), status, "Not a decision an application reason code applies to.");

    /// <summary>Whether <paramref name="code"/> may be recorded against a decision of <paramref name="status"/>.</summary>
    public static bool IsAllowed(ServiceApplicationStatus status, ApplicationReasonCode code) => For(status).Contains(code);

    /// <summary>
    /// Whether <paramref name="code"/> obliges the reviewer to write something as well.
    /// </summary>
    /// <remarks>
    /// True for <see cref="ApplicationReasonCode.Other"/> and for
    /// <see cref="ApplicationReasonCode.ApprovedByException"/>. The second is the departure from
    /// WP-2.15's rule and it is deliberate: an exception that does not say what the exception <i>was</i>
    /// is the one record on an application that has to defend itself, and every other code on this
    /// list already says what happened.
    /// </remarks>
    public static bool RequiresNotes(ApplicationReasonCode code) =>
        code is ApplicationReasonCode.Other or ApplicationReasonCode.ApprovedByException;
}
