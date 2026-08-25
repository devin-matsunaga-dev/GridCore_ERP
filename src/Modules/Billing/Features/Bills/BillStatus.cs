namespace GridCore.Modules.Billing.Features.Bills;

/// <summary>
/// Where a bill stands. The document's own lifecycle — separate from the service account's status
/// and from the customer's: a Closed account can still hold an Overdue bill, and that is exactly
/// the debt somebody has to chase.
/// </summary>
public enum BillStatus
{
    /// <summary>
    /// Calculated but not sent. Where every bill starts: a billing run produces drafts, and issuing
    /// one is a separate act because the exception worklist is worked in between.
    /// </summary>
    Draft = 1,

    /// <summary>Sent to the customer and owed. The point at which Finance posts the receivable.</summary>
    Issued = 2,

    /// <summary>Part of the balance has been paid; the rest is still owed.</summary>
    PartiallyPaid = 3,

    /// <summary>Settled in full. Terminal — money coming back afterwards is a refund, not an unpayment.</summary>
    Paid = 4,

    /// <summary>Still owed after the due date. Not terminal: an overdue bill is paid like any other.</summary>
    Overdue = 5,

    /// <summary>
    /// Withdrawn before it was settled — billed in error, or superseded. Terminal: the cancelled
    /// document keeps saying what it said, and anything owed after it is a new bill. Same reason a
    /// ledger correction is a new entry rather than an edit (invariant 3).
    /// </summary>
    Cancelled = 6,
}

/// <summary>
/// The bill state machine, in one place. Kept out of <see cref="Bill"/> so a UI can ask what is
/// legal without holding an entity, matching <c>MeterTransitions</c> and
/// <c>ServiceAccountTransitions</c>.
/// </summary>
public static class BillTransitions
{
    private static readonly Dictionary<BillStatus, BillStatus[]> Allowed = new()
    {
        // A draft is not owed by anybody: it can be sent, or thrown away. It cannot be paid,
        // because nobody has been asked for the money.
        [BillStatus.Draft] = [BillStatus.Issued, BillStatus.Cancelled],

        [BillStatus.Issued] = [BillStatus.PartiallyPaid, BillStatus.Paid, BillStatus.Overdue, BillStatus.Cancelled],
        [BillStatus.PartiallyPaid] = [BillStatus.Paid, BillStatus.Overdue, BillStatus.Cancelled],

        // No Overdue -> Issued: a bill does not stop having been late. It is paid, part-paid, or
        // withdrawn.
        [BillStatus.Overdue] = [BillStatus.PartiallyPaid, BillStatus.Paid, BillStatus.Cancelled],

        [BillStatus.Paid] = [],
        [BillStatus.Cancelled] = [],
    };

    /// <summary>The statuses a bill in <paramref name="status"/> may move to.</summary>
    public static IReadOnlyList<BillStatus> AllowedFrom(BillStatus status) =>
        Allowed.TryGetValue(status, out var next) ? next : [];

    /// <summary>Whether <paramref name="from"/> → <paramref name="to"/> is a legal move.</summary>
    public static bool IsAllowed(BillStatus from, BillStatus to) => AllowedFrom(from).Contains(to);

    /// <summary>
    /// Whether a bill in <paramref name="status"/> is money the utility is still owed — what an AR
    /// balance sums and what an overdue review looks at.
    /// </summary>
    /// <remarks>
    /// A draft is not outstanding: it has not been sent, so nobody owes it. That is the difference
    /// between a receivable and a calculation, and it is why Finance posts on
    /// <c>BillIssued</c> rather than when the run produces the figures.
    /// </remarks>
    public static bool IsOutstanding(BillStatus status) =>
        status is BillStatus.Issued or BillStatus.PartiallyPaid or BillStatus.Overdue;

    /// <summary>Whether a bill in <paramref name="status"/> can never move again.</summary>
    public static bool IsFinal(BillStatus status) => AllowedFrom(status).Count is 0;
}
