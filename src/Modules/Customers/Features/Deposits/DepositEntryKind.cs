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

        // Not a default that guesses. A kind added without a direction would silently take the
        // sign of whichever branch was written first, and the balance would be wrong in a way no
        // test asked about.
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a deposit entry kind GridCore declares."),
    };

    /// <summary>Whether <paramref name="kind"/> takes money off what the utility holds.</summary>
    public static bool ReducesBalance(DepositEntryKind kind) => DirectionOf(kind) < 0;
}
