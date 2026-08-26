namespace GridCore.Modules.Customers.Features.Deposits;

/// <summary>
/// What one movement of a customer's security deposit was.
/// </summary>
/// <remarks>
/// <para>
/// <b>The kind carries the direction; an amount is never signed.</b> Every entry stores a positive
/// magnitude and this says which way it moved, so a collection of minus fifty is not a shape the
/// ledger can hold. It is the call <c>BillAdjustmentKind</c> already makes for a bill's corrections
/// and <c>FinancePostings</c> makes for a journal line — a negative debit and a credit are the same
/// money, and only one of them can be added up by eye.
/// </para>
/// <para>
/// <b>There is no <c>Held</c> member, because holding is not a movement.</b> WORK_PACKAGES.md lists
/// hold as a stage of the lifecycle, and it is — but it is the state between two movements, which
/// the running balance already says. An entry saying "and then nothing happened" would be a row
/// nobody could reconcile against a bank statement.
/// </para>
/// <para>
/// Adding a member means giving it a direction below, a Finance posting, and a migration only if
/// the stored name is new — the column stores the name, so the numbering here is never load-bearing.
/// </para>
/// </remarks>
public enum DepositEntryKind
{
    /// <summary>Money taken from the customer and held against their account. Increases the balance.</summary>
    Collected,

    /// <summary>Money held put against a bill the customer owes. Decreases the balance.</summary>
    Applied,

    /// <summary>Money given back to the customer. Decreases the balance.</summary>
    Refunded,

    /// <summary>
    /// The held deposit was carried from one of the customer's service accounts to another on a
    /// transfer (WP-2.15). <b>Leaves the balance exactly where it was.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one kind with no direction, and it is not the exception it looks like.</b> A deposit is
    /// held against the <i>customer</i>, and both accounts on a transfer are that same customer's —
    /// so nothing left the utility and nothing arrived. Synthesising a refund and a collection at the
    /// same instant would balance to the same figure and lie twice about what happened: a customer's
    /// statement would show money going out and coming back, and every "a refund cannot exceed the
    /// held balance" guard would have to learn about a refund that was not one.
    /// </para>
    /// <para>
    /// The entry is written for the record, not for the arithmetic. It is what makes a deposit that
    /// survived a house move readable as having survived it, rather than as a balance that silently
    /// stayed put. A customer holding <b>nothing</b> gets no entry at all — a movement of zero is a
    /// row nobody can reconcile, which is the same argument this enum makes above for having no
    /// <c>Held</c> member.
    /// </para>
    /// </remarks>
    Transferred,
}

/// <summary>Which way each <see cref="DepositEntryKind"/> moves the balance.</summary>
public static class DepositEntryKinds
{
    /// <summary>
    /// <c>+1</c> for money coming in, <c>-1</c> for money going out — the multiplier that turns an
    /// entry's magnitude into its effect on what the utility holds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not one GridCore declares.</exception>
    public static int DirectionOf(DepositEntryKind kind) => kind switch
    {
        DepositEntryKind.Collected => 1,
        DepositEntryKind.Applied => -1,
        DepositEntryKind.Refunded => -1,

        // Zero, and the only zero. See the member's own remarks: a transfer moves a deposit between
        // two of one customer's accounts, and the customer is who the deposit is held against.
        DepositEntryKind.Transferred => 0,

        // Not a default that guesses. A kind added without a direction would silently take the
        // sign of whichever branch was written first, and the balance would be wrong in a way no
        // test asked about.
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a deposit entry kind GridCore declares."),
    };

    /// <summary>Whether <paramref name="kind"/> takes money off what the utility holds.</summary>
    public static bool ReducesBalance(DepositEntryKind kind) => DirectionOf(kind) < 0;

    /// <summary>
    /// Whether <paramref name="kind"/> leaves the balance exactly where it was.
    /// </summary>
    /// <remarks>
    /// Asked by the account statement, which has to carry a zero-effect movement forward on both of
    /// its columns rather than skip it — a line whose printed balance disagrees with the running
    /// total is precisely what <c>AccountStatement.Compose</c> refuses to produce.
    /// </remarks>
    public static bool MovesNothing(DepositEntryKind kind) => DirectionOf(kind) == 0;
}
