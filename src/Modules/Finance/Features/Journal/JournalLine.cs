using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Modules.Finance.Features.Shared;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Finance.Features.Journal;

/// <summary>
/// One side of one journal entry: an account, and the amount debited or credited to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A line carries a debit or a credit, never both.</b> Two amounts netted into one line is a
/// ledger that has stopped explaining itself — and it is also how a trial balance quietly comes out
/// right while the story behind it is wrong. Both columns are non-negative; a posting the other way
/// is the other side of the entry, never a negative one.
/// </para>
/// <para>
/// <b>The account is a foreign key, not a stamped code.</b> Every other cross-reference in GridCore
/// is stamped — a bill stamps its tariff's rate, a payment stamps the customer's name — because the
/// thing referred to lives in another module and will move. The chart of accounts is neither: it is
/// reference data in <i>this</i> schema, shipped by migration, and an account's code is its
/// identity and never changes. So the line points at the row, the database refuses a posting to an
/// account that does not exist, and there is no second copy of the code to disagree with the first.
/// This is the lookup WP-0.8 said WP-2.6 owed.
/// </para>
/// <para>
/// There is no per-line description. What an entry is for is said once, on the entry; what a line
/// is for is the account it names. A second free-text field nothing writes is a column WP-4.2 would
/// have to render and nobody could explain.
/// </para>
/// </remarks>
public sealed class JournalLine
{
    private JournalLine()
    {
        // EF materialisation.
        Account = null!;
    }

    /// <summary>Identifier of this line. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The entry it belongs to.</summary>
    public Guid JournalEntryId { get; private init; }

    /// <summary>
    /// Position within the entry, from 1. Ordered on explicitly rather than on the key: the lines of
    /// one entry are minted in the same clock instant, and Guid v7 gives them no defined order.
    /// </summary>
    public int Sequence { get; private init; }

    /// <summary>The account posted to.</summary>
    public Guid AccountId { get; private init; }

    /// <summary>That account, from the chart in this schema.</summary>
    public Account Account { get; private init; }

    /// <summary>Amount debited. Zero on a credit line. Money is <see langword="decimal"/>.</summary>
    public decimal Debit { get; private init; }

    /// <summary>Amount credited. Zero on a debit line. Money is <see langword="decimal"/>.</summary>
    public decimal Credit { get; private init; }

    /// <summary>What the line comes to, whichever side it is on.</summary>
    public decimal Amount => Debit + Credit;

    /// <summary>Whether the money is on the debit side.</summary>
    public bool IsDebit => Debit != Money.Zero;

    /// <summary>
    /// Writes <paramref name="line"/> onto <paramref name="journalEntryId"/> against a chart row.
    /// </summary>
    /// <remarks>
    /// Internal, and takes an account the entry has already resolved: whether a code is one the
    /// chart declares is a question about the whole posting, and answering it a line at a time would
    /// mean one query per line.
    /// </remarks>
    /// <exception cref="FinanceValidationException">The line is two-sided, empty, negative or finer than a cent.</exception>
    internal static JournalLine Post(
        Guid journalEntryId,
        int sequence,
        Account account,
        JournalLineIntent line,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(line);

        // JournalPostingIntent.For has already refused a two-sided, negative or sub-cent line. This
        // is the aggregate's own guard, which also protects a caller reaching the ledger directly —
        // the same reason Bill.Calculate re-asserts a total the rate engine already computed.
        if (line.Debit < Money.Zero || line.Credit < Money.Zero)
        {
            throw new FinanceValidationException(
                $"A journal line on account {account.Code} may not carry a negative amount.");
        }

        if ((line.Debit == Money.Zero) == (line.Credit == Money.Zero))
        {
            throw new FinanceValidationException(
                $"A journal line on account {account.Code} carries exactly one side — "
                + "it is a debit or a credit, never both and never neither.");
        }

        if (!Money.IsRounded(line.Amount))
        {
            throw new FinanceValidationException(
                $"'{line.Amount}' on account {account.Code} is finer than a cent; the ledger holds whole cents.");
        }

        return new JournalLine
        {
            Id = Guid.CreateVersion7(now),
            JournalEntryId = journalEntryId,
            Sequence = sequence,
            AccountId = account.Id,
            Account = account,
            Debit = line.Debit,
            Credit = line.Credit,
        };
    }
}
