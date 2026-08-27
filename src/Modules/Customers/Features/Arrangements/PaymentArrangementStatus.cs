namespace GridCore.Modules.Customers.Features.Arrangements;

/// <summary>
/// Where a payment arrangement stands (WP-2.20).
/// </summary>
/// <remarks>
/// Persisted by name, like every other enum in this schema, so an arrangement made this year still
/// reads the same after the numbering moves.
/// </remarks>
public enum PaymentArrangementStatus
{
    /// <summary>
    /// Offered and not yet in force. An arrangement over the rep's published limit waits here until
    /// somebody with the authority approves it — see <see cref="ArrangementLimit"/>.
    /// </summary>
    Proposed = 0,

    /// <summary>
    /// In force. <b>The only state that suppresses disconnection</b>, which is the one thing about
    /// this feature that anything outside it can see.
    /// </summary>
    Active = 1,

    /// <summary>Every instalment settled. Terminal.</summary>
    Kept = 2,

    /// <summary>
    /// An instalment passed its due date unpaid. Terminal: a broken arrangement is replaced, never
    /// resumed — WORK_PACKAGES.md asks for exactly that.
    /// </summary>
    Broken = 3,
}

/// <summary>
/// The arrangement state machine, as a pure function so the rules read and test without a database
/// — the shape <c>ApprovalTransitions</c> and <c>ServiceAccountTransitions</c> both take.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both terminal states are final, and that is the package's sharpest rule.</b> "A broken
/// arrangement cannot be resumed, only replaced" is what stops a customer who has missed three
/// instalments being carried indefinitely by a rep who keeps reviving the same promise: the record
/// of the broken one stays, and a fresh promise is a fresh row somebody had to decide to make.
/// </para>
/// <para>
/// <b>There is no path back to <see cref="PaymentArrangementStatus.Proposed"/>.</b> An arrangement
/// that was offered and never taken up is left as it is rather than cancelled, because the register
/// is a record of what was offered — and the account's protection depends on <c>Active</c>, so a
/// stale proposal protects nobody.
/// </para>
/// </remarks>
public static class PaymentArrangementTransitions
{
    /// <summary>Whether <paramref name="from"/> may become <paramref name="to"/>.</summary>
    public static bool IsAllowed(PaymentArrangementStatus from, PaymentArrangementStatus to) =>
        (from, to) switch
        {
            (PaymentArrangementStatus.Proposed, PaymentArrangementStatus.Active) => true,
            (PaymentArrangementStatus.Active, PaymentArrangementStatus.Kept) => true,
            (PaymentArrangementStatus.Active, PaymentArrangementStatus.Broken) => true,
            _ => false,
        };

    /// <summary>The states <paramref name="from"/> may move to, for a UI that renders them as buttons.</summary>
    public static IReadOnlyList<PaymentArrangementStatus> AllowedFrom(PaymentArrangementStatus from) =>
        [.. Enum.GetValues<PaymentArrangementStatus>().Where(to => IsAllowed(from, to))];

    /// <summary>Whether <paramref name="status"/> is final.</summary>
    public static bool IsTerminal(PaymentArrangementStatus status) =>
        status is PaymentArrangementStatus.Kept or PaymentArrangementStatus.Broken;
}
