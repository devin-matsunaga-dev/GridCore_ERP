namespace GridCore.Modules.Finance.Features.EventSeam;

/// <summary>
/// Account codes the seam posts to. Placeholders: WP-0.8 ships the real chart of accounts by
/// migration, and WP-2.6 replaces these constants with a lookup against it. They are named here so
/// the seam produces a recognisable posting today rather than an untyped blob.
/// </summary>
public static class FinanceAccounts
{
    /// <summary>Cash at bank.</summary>
    public const string Cash = "1000";

    /// <summary>Accounts receivable — what customers owe.</summary>
    public const string AccountsReceivable = "1100";

    /// <summary>Inventory held in warehouses.</summary>
    public const string Inventory = "1300";

    /// <summary>Accounts payable — what we owe vendors.</summary>
    public const string AccountsPayable = "2000";

    /// <summary>Utility revenue.</summary>
    public const string Revenue = "4000";
}

/// <summary>One side of a journal entry: an account and the amount debited or credited to it.</summary>
/// <param name="AccountCode">The account posted to.</param>
/// <param name="Debit">Amount debited. Money is <see langword="decimal"/>, never a float.</param>
/// <param name="Credit">Amount credited. Money is <see langword="decimal"/>, never a float.</param>
public sealed record JournalLineIntent(string AccountCode, decimal Debit, decimal Credit)
{
    /// <summary>A line that debits an account.</summary>
    public static JournalLineIntent Debits(string accountCode, decimal amount) => new(accountCode, amount, 0m);

    /// <summary>A line that credits an account.</summary>
    public static JournalLineIntent Credits(string accountCode, decimal amount) => new(accountCode, 0m, amount);
}

/// <summary>
/// What a domain event means to the ledger: the journal entry Finance would post for it. Built by
/// <see cref="FinancePostings"/> from an event and handed to <see cref="IJournalPostingSeam"/>.
/// </summary>
/// <param name="EventId">The event that caused it, so the posting is traceable to its cause.</param>
/// <param name="OccurredAt">When the fact became true — the posting date.</param>
/// <param name="Source">Which event this came from, e.g. <c>billing.bill_issued</c>.</param>
/// <param name="Reference">The business reference a person would recognise: bill number, provider reference.</param>
/// <param name="Description">One line describing the entry.</param>
/// <param name="Currency">ISO 4217 code the amounts are expressed in.</param>
/// <param name="Lines">The debits and credits. Always balanced — the factory refuses otherwise.</param>
public sealed record JournalPostingIntent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string Source,
    string Reference,
    string Description,
    string Currency,
    IReadOnlyList<JournalLineIntent> Lines)
{
    /// <summary>Sum of the debits.</summary>
    public decimal TotalDebits => Lines.Sum(line => line.Debit);

    /// <summary>Sum of the credits.</summary>
    public decimal TotalCredits => Lines.Sum(line => line.Credit);

    /// <summary>
    /// Builds a posting, refusing to build an unbalanced one.
    /// </summary>
    /// <remarks>
    /// Invariant 3 of ARCHITECTURE.md, enforced at the only place postings are constructed. An
    /// unbalanced entry is a defect in the mapping, and a defect that throws in a millisecond-long
    /// unit test is worth a great deal more than one discovered in a trial balance.
    /// </remarks>
    /// <exception cref="ArgumentException">There are no lines, or debits do not equal credits.</exception>
    public static JournalPostingIntent For(
        Guid eventId,
        DateTimeOffset occurredAt,
        string source,
        string reference,
        string description,
        string currency,
        IReadOnlyList<JournalLineIntent> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        if (lines.Count is 0)
        {
            throw new ArgumentException($"Journal posting from {source} has no lines.", nameof(lines));
        }

        var debits = lines.Sum(line => line.Debit);
        var credits = lines.Sum(line => line.Credit);

        if (debits != credits)
        {
            throw new ArgumentException(
                $"Journal posting from {source} does not balance: debits {debits} != credits {credits}. "
                + "The ledger only ever holds balanced entries.",
                nameof(lines));
        }

        return new JournalPostingIntent(eventId, occurredAt, source, reference, description, currency, lines);
    }
}
