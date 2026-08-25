namespace GridCore.Modules.Payments.Features.Payments;

/// <summary>
/// Where a payment stands. The attempt's own lifecycle — separate from the bill's and from the
/// account's: a Paid bill is the consequence of an Approved payment, never the same fact, and a
/// Closed account can still hold a payment somebody is querying.
/// </summary>
/// <remarks>
/// Deliberately shorter than <c>PaymentOutcome</c>. The provider distinguishes a decline from a
/// shortfall and GridCore stores both answers, but what a payment <i>is</i> afterwards is one of
/// four things — and a status set with one member per provider reason is a status every screen has
/// to translate before it can render.
/// </remarks>
public enum PaymentStatus
{
    /// <summary>
    /// Taken, not yet answered. Where every payment starts and where none should linger: the
    /// provider is asked inside the same act that records it.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// The money moved. The only status that reduces what a customer owes, and the only one that
    /// publishes <c>PaymentApproved</c> for Finance and Billing.
    /// </summary>
    Approved = 2,

    /// <summary>
    /// Refused — by the issuer, or for want of funds. The reason is on the payment's outcome; the
    /// status is the same either way because the consequence is: no money moved, nothing to post.
    /// </summary>
    Declined = 3,

    /// <summary>
    /// The provider did not answer. <b>Not a decline.</b> The money may have moved and the answer
    /// been lost, so nothing may be assumed about the customer's balance until somebody reconciles
    /// against the provider — which is why this is its own status rather than a kind of refusal.
    /// </summary>
    Failed = 4,

    /// <summary>
    /// Money returned to the customer. Terminal, and unreachable in the MVP: nothing refunds yet.
    /// It is in the machine so the transition an approved payment may make is written down where a
    /// screen can read it, rather than discovered when the refund work package arrives.
    /// </summary>
    Refunded = 5,
}

/// <summary>
/// The payment state machine, in one place. Kept out of <see cref="Payment"/> so a UI can ask what
/// is legal without holding an entity, matching <c>BillTransitions</c> and
/// <c>ServiceAccountTransitions</c>.
/// </summary>
public static class PaymentTransitions
{
    private static readonly Dictionary<PaymentStatus, PaymentStatus[]> Allowed = new()
    {
        // The provider's answer, and nothing else, moves a pending payment.
        [PaymentStatus.Pending] = [PaymentStatus.Approved, PaymentStatus.Declined, PaymentStatus.Failed],

        // Money that arrived can go back. Nothing in the MVP performs this — payments.refund is
        // still claimed by no route — but it is the one move an approved payment has.
        [PaymentStatus.Approved] = [PaymentStatus.Refunded],

        // A refusal is final. The customer tries again, which is a new attempt with its own
        // instrument, its own provider reference and its own row — never this one revived. Same
        // call BillStatus makes about a cancelled bill.
        [PaymentStatus.Declined] = [],

        // Final too, and pointedly so: a payment whose answer was lost is reconciled against the
        // provider and settled by a new attempt or a manual entry, never by guessing here.
        [PaymentStatus.Failed] = [],

        [PaymentStatus.Refunded] = [],
    };

    /// <summary>The statuses a payment in <paramref name="status"/> may move to.</summary>
    public static IReadOnlyList<PaymentStatus> AllowedFrom(PaymentStatus status) =>
        Allowed.TryGetValue(status, out var next) ? next : [];

    /// <summary>Whether <paramref name="from"/> → <paramref name="to"/> is a legal move.</summary>
    public static bool IsAllowed(PaymentStatus from, PaymentStatus to) => AllowedFrom(from).Contains(to);

    /// <summary>
    /// Whether a payment in <paramref name="status"/> is money the utility actually holds — what an
    /// approved-receipts total sums, and what makes a bill's balance move.
    /// </summary>
    /// <remarks>
    /// <see cref="PaymentStatus.Refunded"/> is not settled money: it arrived and went back, so a
    /// total that counted it would overstate the day's takings by twice the refund.
    /// </remarks>
    public static bool IsSettled(PaymentStatus status) => status is PaymentStatus.Approved;

    /// <summary>Whether a payment in <paramref name="status"/> can never move again.</summary>
    public static bool IsFinal(PaymentStatus status) => AllowedFrom(status).Count is 0;
}
