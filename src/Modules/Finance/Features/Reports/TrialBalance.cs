using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Finance.Features.Reports;

/// <summary>One account's position on a trial balance.</summary>
/// <param name="AccountCode">The code a person quotes.</param>
/// <param name="AccountName">What the account is called on a report.</param>
/// <param name="Type">Which of the five kinds it is.</param>
/// <param name="NormalBalance">Which side it is normally increased on.</param>
/// <param name="Debits">Everything debited to it in the period read.</param>
/// <param name="Credits">Everything credited to it.</param>
/// <param name="LineCount">How many ledger lines are behind those two figures.</param>
public sealed record TrialBalanceRow(
    string AccountCode,
    string AccountName,
    AccountType Type,
    NormalBalance NormalBalance,
    decimal Debits,
    decimal Credits,
    int LineCount)
{
    /// <summary>
    /// What the account stands at, signed the way the account normally runs — so a receivables
    /// balance and a revenue balance are both positive when the utility is having an ordinary month,
    /// and a negative figure means something worth looking at rather than an asset account being an
    /// asset account.
    /// </summary>
    public decimal Balance => NormalBalance is NormalBalance.Debit
        ? Debits - Credits
        : Credits - Debits;

    /// <summary>Whether anything has been posted to this account at all.</summary>
    public bool HasActivity => LineCount > 0;
}

/// <summary>
/// The trial balance: every account in the chart, what has been debited and credited to it, and the
/// proof that the two columns agree.
/// </summary>
/// <remarks>
/// <b>Every account is listed, including the ones nothing has touched.</b> A trial balance that
/// showed only accounts with activity would change shape as the demo ran, and a reader could not
/// tell an account with no postings from an account somebody forgot to ship. Fifteen rows is a
/// report, not a scan.
/// </remarks>
/// <param name="AsOf">The accounting date read up to, inclusive.</param>
/// <param name="Rows">Every account, in code order.</param>
public sealed record TrialBalance(DateOnly AsOf, IReadOnlyList<TrialBalanceRow> Rows)
{
    /// <summary>Everything debited across the whole ledger.</summary>
    public decimal TotalDebits => Money.Total(Rows.Select(row => row.Debits));

    /// <summary>Everything credited across it.</summary>
    public decimal TotalCredits => Money.Total(Rows.Select(row => row.Credits));

    /// <summary>
    /// Whether the ledger balances — the one number this report exists to produce.
    /// </summary>
    /// <remarks>
    /// It is true of any ledger built by <see cref="Journal.JournalEntry.Post"/>, which refuses an
    /// unbalanced entry, and the sum of balanced entries is balanced. That is exactly why it is
    /// worth computing and asserting: if it ever comes out false, something has written to these
    /// tables without going through the aggregate.
    /// </remarks>
    public bool IsBalanced => TotalDebits == TotalCredits;

    /// <summary>How far out of balance the ledger is. Zero, unless something is very wrong.</summary>
    public decimal Difference => TotalDebits - TotalCredits;
}
