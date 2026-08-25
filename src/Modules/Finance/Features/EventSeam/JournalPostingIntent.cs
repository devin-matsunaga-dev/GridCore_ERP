using GridCore.Platform.Monetary;

namespace GridCore.Modules.Finance.Features.EventSeam;

/// <summary>One side of a journal entry: an account and the amount debited or credited to it.</summary>
/// <remarks>
/// A line carries a debit <i>or</i> a credit, never both and never neither — a two-sided line is
/// two lines that have been netted off, and netting is how a ledger stops explaining itself.
/// <see cref="JournalPostingIntent.For"/> refuses one, so the rule holds even for a line built by
/// hand rather than through the factories below.
/// </remarks>
/// <param name="AccountCode">The account posted to.</param>
/// <param name="Debit">Amount debited. Money is <see langword="decimal"/>, never a float.</param>
/// <param name="Credit">Amount credited. Money is <see langword="decimal"/>, never a float.</param>
public sealed record JournalLineIntent(string AccountCode, decimal Debit, decimal Credit)
{
    /// <summary>What the line comes to, whichever side it is on.</summary>
    public decimal Amount => Debit + Credit;

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
/// <param name="ServiceAccountId">
/// The service account the entry is about, where it is about one. This is the subsidiary dimension
/// an AR view is built from: the receivables control account says what the utility is owed in
/// total, and these say by whom. Absent on an entry that concerns no single account — a vendor
/// payable, say, whose subsidiary is a vendor and belongs to WP-4.1.
/// </param>
/// <param name="CustomerId">The customer the entry is about, carried alongside the account for the same reason.</param>
public sealed record JournalPostingIntent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string Source,
    string Reference,
    string Description,
    string Currency,
    IReadOnlyList<JournalLineIntent> Lines,
    Guid? ServiceAccountId = null,
    Guid? CustomerId = null)
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
    /// <exception cref="ArgumentException">
    /// There are no lines, a line is not one-sided, an amount is negative or finer than a cent, or
    /// debits do not equal credits.
    /// </exception>
    public static JournalPostingIntent For(
        Guid eventId,
        DateTimeOffset occurredAt,
        string source,
        string reference,
        string description,
        string currency,
        IReadOnlyList<JournalLineIntent> lines,
        Guid? serviceAccountId = null,
        Guid? customerId = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        if (lines.Count is 0)
        {
            throw new ArgumentException($"Journal posting from {source} has no lines.", nameof(lines));
        }

        foreach (var line in lines)
        {
            RequireOneSidedLine(source, line);
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

        return new JournalPostingIntent(
            eventId,
            occurredAt,
            source,
            reference,
            description,
            currency,
            lines,
            serviceAccountId,
            customerId);
    }

    /// <summary>
    /// Checks one line is a debit or a credit of a real, positive, whole-cent amount.
    /// </summary>
    /// <remarks>
    /// The rounding check is deliberately a <i>refusal</i> rather than a rounding step, which is the
    /// rule <see cref="Money"/> states: the amounts arriving here were computed and rounded upstream
    /// — a bill line by the rate engine, a payment by the register — so one that is finer than a
    /// cent means an upstream total that no longer adds up, and quietly rounding it here would hide
    /// exactly that.
    /// </remarks>
    /// <exception cref="ArgumentException">The line is two-sided, empty, negative or finer than a cent.</exception>
    private static void RequireOneSidedLine(string source, JournalLineIntent line)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(line.AccountCode);

        if (line.Debit < Money.Zero || line.Credit < Money.Zero)
        {
            throw new ArgumentException(
                $"Journal posting from {source} has a negative amount on account {line.AccountCode}. "
                + "A posting the other way is the other side of the entry, never a negative one.",
                nameof(line));
        }

        if ((line.Debit == Money.Zero) == (line.Credit == Money.Zero))
        {
            throw new ArgumentException(
                $"Journal posting from {source} has a line on account {line.AccountCode} that is "
                + "neither a debit nor a credit, or is both. A line carries exactly one side.",
                nameof(line));
        }

        if (!Money.IsRounded(line.Amount))
        {
            throw new ArgumentException(
                $"Journal posting from {source} has '{line.Amount}' on account {line.AccountCode}, "
                + "which is finer than a cent. The ledger holds what was billed and paid, to the cent.",
                nameof(line));
        }
    }
}
