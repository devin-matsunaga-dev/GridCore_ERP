using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Modules.Finance.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Finance.Features.Journal;

/// <summary>
/// One balanced journal entry and the lines that make it up — the general ledger.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only, and balanced or nothing.</b> Invariant 3 of ARCHITECTURE.md lives here:
/// <see cref="Post"/> throws unless debits equal credits, and <c>FinanceDbContext</c> throws if an
/// entry or a line is ever modified or deleted. A correction is a new entry — which is exactly what
/// a <c>BillAdjusted</c> posting is, and why Billing publishes the <i>change</i> rather than a
/// replacement figure.
/// </para>
/// <para>
/// <b>The entry carries the party, not the line.</b> A subsidiary ledger — what this customer owes —
/// is built by reading the receivables lines and grouping them by the account and customer on their
/// entry. Every posting GridCore makes concerns one party at most, so the dimension belongs on the
/// entry: putting it on the line would let one entry name two customers, which is a batch posting
/// nothing raises and an AR view nobody could reconcile. A vendor payable carries neither, because
/// its subsidiary is a vendor and that ledger is WP-4.1's.
/// </para>
/// <para>
/// <b>Nothing here holds a balance.</b> An account's balance is the sum of its lines, computed on
/// demand by the trial balance — never a column that could drift from the entries that explain it,
/// the same call <see cref="Account"/> made about its normal balance.
/// </para>
/// </remarks>
public sealed class JournalEntry
{
    /// <summary>Longest source name stored, e.g. <c>billing.bill_issued</c>.</summary>
    public const int SourceLength = 64;

    /// <summary>Longest business reference stored — a bill number, a provider reference.</summary>
    public const int ReferenceLength = 64;

    /// <summary>Longest entry description stored.</summary>
    public const int DescriptionLength = 512;

    /// <summary>Length of an ISO 4217 currency code.</summary>
    public const int CurrencyLength = 3;

    /// <summary>Total digits a money column stores.</summary>
    public const int MoneyPrecision = Money.Precision;

    /// <summary>Decimal places a money column stores — the cent.</summary>
    public const int MoneyScale = Money.DecimalPlaces;

    private readonly List<JournalLine> _lines = [];

    private JournalEntry()
    {
        // EF materialisation.
        EntryNumber = string.Empty;
        Source = string.Empty;
        Reference = string.Empty;
        Description = string.Empty;
        Currency = string.Empty;
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this entry. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The number on the entry, e.g. <c>JRN-000001</c>. Unique across the ledger.</summary>
    public string EntryNumber { get; private init; }

    /// <summary>
    /// The event that caused it, so an entry is traceable back to the fact behind it. Unique, which
    /// is the database's own answer to a redelivery that somehow got past the dedupe claim.
    /// <see langword="null"/> only for an entry raised by hand, which nothing does yet.
    /// </summary>
    public Guid? EventId { get; private init; }

    /// <summary>Which upstream fact this came from, e.g. <c>billing.bill_issued</c>.</summary>
    public string Source { get; private init; }

    /// <summary>The business reference a person would recognise: a bill number, a provider reference.</summary>
    public string Reference { get; private init; }

    /// <summary>One line saying what the entry is for.</summary>
    public string Description { get; private init; }

    /// <summary>ISO 4217 code every line is expressed in.</summary>
    public string Currency { get; private init; }

    /// <summary>
    /// The accounting date: the day the fact became true, not the day Finance heard about it. A
    /// redelivery replayed a week later posts to the day the bill was issued, which is the whole
    /// reason the event carries its own timestamp.
    /// </summary>
    public DateOnly PostedOn { get; private init; }

    /// <summary>When the fact became true, to the instant.</summary>
    public DateTimeOffset OccurredAt { get; private init; }

    /// <summary>When Finance wrote the entry.</summary>
    public DateTimeOffset PostedAt { get; private init; }

    /// <summary>The service account this entry is about, where it is about one.</summary>
    public Guid? ServiceAccountId { get; private init; }

    /// <summary>The customer this entry is about, where it is about one.</summary>
    public Guid? CustomerId { get; private init; }

    /// <summary>Sum of the debit lines, stored so a ledger listing does not have to load them.</summary>
    public decimal TotalDebits { get; private init; }

    /// <summary>Sum of the credit lines. Equal to <see cref="TotalDebits"/>, always.</summary>
    public decimal TotalCredits { get; private init; }

    /// <summary>Subject id of whoever posted it — <c>system</c> for an entry raised by a consumer.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time, where one was known.</summary>
    public string? ActorName { get; private init; }

    /// <summary>The debits and credits, in the order they were posted.</summary>
    public IReadOnlyList<JournalLine> Lines => _lines;

    /// <summary>Whether the entry balances. True of every entry that exists — <see cref="Post"/> refuses the rest.</summary>
    public bool IsBalanced => TotalDebits == TotalCredits;

    /// <summary>
    /// Posts <paramref name="posting"/> to the ledger against the chart rows in
    /// <paramref name="accounts"/>, keyed by account code.
    /// </summary>
    /// <remarks>
    /// The balance check is re-asserted here even though <see cref="JournalPostingIntent.For"/>
    /// already refused an unbalanced intent. CONVENTIONS.md asks for the assertion at the posting
    /// ("assert it in code — throw if debits≠credits"), and an invariant this expensive to discover
    /// late is worth two cheap guards: the mapping's, and the ledger's own.
    /// </remarks>
    /// <exception cref="FinanceValidationException">
    /// The posting names an account the chart does not declare, has no lines, or does not balance.
    /// </exception>
    public static JournalEntry Post(
        string entryNumber,
        JournalPostingIntent posting,
        IReadOnlyDictionary<string, Account> accounts,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(posting);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(actor);

        if (posting.Lines.Count is 0)
        {
            throw new FinanceValidationException(
                $"The posting from {posting.Source} has no lines; an entry with nothing on it is not a fact.");
        }

        // The id is minted first because the lines carry it, and the lines are built before the
        // entry so the totals it stores are the totals of the lines it actually holds.
        var id = Guid.CreateVersion7(now);
        var lines = new List<JournalLine>(posting.Lines.Count);
        var sequence = 1;

        foreach (var line in posting.Lines)
        {
            if (!accounts.TryGetValue(line.AccountCode, out var account))
            {
                // The chart is reference data shipped by migration; adding an account is a migration
                // and never a runtime insert, so a code that is not there is a defect in the mapping
                // rather than a row somebody forgot to create.
                throw new FinanceValidationException(
                    $"'{line.AccountCode}' is not an account in the chart, so the posting from "
                    + $"{posting.Source} cannot be made. Accounts are reference data; adding one is a migration.");
            }

            lines.Add(JournalLine.Post(id, sequence++, account, line, now));
        }

        var totalDebits = Money.Total(lines.Select(line => line.Debit));
        var totalCredits = Money.Total(lines.Select(line => line.Credit));

        // INVARIANT 3, at the ledger's own door. Not reachable through JournalPostingIntent, which
        // refuses an unbalanced posting before one can be built — which is the point: by the time an
        // entry is being written there is nothing left that could make it unbalanced, and this
        // throws if that ever stops being true.
        if (totalDebits != totalCredits)
        {
            throw new FinanceValidationException(
                $"The entry from {posting.Source} does not balance: debits {totalDebits} != "
                + $"credits {totalCredits}. The ledger only ever holds balanced entries.");
        }

        var entry = new JournalEntry
        {
            Id = id,
            EntryNumber = RegistryText.Clean(entryNumber, RegistryNumbers.MaxLength)
                ?? throw new FinanceValidationException("A journal entry must carry an entry number."),
            EventId = posting.EventId == Guid.Empty ? null : posting.EventId,
            Source = RegistryText.Clean(posting.Source, SourceLength)
                ?? throw new FinanceValidationException("A journal entry must say what it came from."),
            Reference = RegistryText.Clean(posting.Reference, ReferenceLength) ?? string.Empty,
            Description = RegistryText.Clean(posting.Description, DescriptionLength) ?? string.Empty,
            Currency = RequireCurrency(posting.Currency),
            PostedOn = DateOnly.FromDateTime(posting.OccurredAt.UtcDateTime),
            OccurredAt = posting.OccurredAt,
            PostedAt = now,
            ServiceAccountId = posting.ServiceAccountId,
            CustomerId = posting.CustomerId,
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new FinanceValidationException("A journal entry must name who posted it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            TotalDebits = totalDebits,
            TotalCredits = totalCredits,
        };

        entry._lines.AddRange(lines);

        return entry;
    }

    private static string RequireCurrency(string currency)
    {
        var cleaned = RegistryText.Clean(currency, CurrencyLength);

        return cleaned?.Length == CurrencyLength
            ? cleaned.ToUpperInvariant()
            : throw new FinanceValidationException(
                $"'{currency}' is not an ISO 4217 currency code; a journal entry is expressed in one.");
    }
}
