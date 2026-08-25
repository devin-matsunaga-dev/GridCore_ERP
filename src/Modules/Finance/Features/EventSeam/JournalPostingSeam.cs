using GridCore.Modules.Finance.Data;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.Journal;
using GridCore.Modules.Finance.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Finance.Features.EventSeam;

/// <summary>
/// The general ledger behind the seam: turns a posting into a balanced, audited, append-only
/// journal entry in <c>finance.journal_entries</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is what WP-0.5 left a <see cref="LoggingJournalPostingSeam"/> standing in for, and nothing
/// upstream changed to make it real — Billing and Payments publish the same events they always did,
/// and the swap is one DI registration, exactly as ARCHITECTURE.md promised for the provider
/// interfaces.
/// </para>
/// <para>
/// <b>It writes and does not commit.</b> Every posting arrives inside the consumer's unit of work,
/// which already holds the dedupe claim that says the event was handled — so the entry and the
/// claim commit together or neither does. The <see cref="IUnitOfWork.ExecuteAsync"/> below is
/// therefore almost always a nested no-op; it is here so a caller reaching the ledger outside a
/// consumer still gets one transaction rather than a half-written entry.
/// </para>
/// <para>
/// <b>Accounts are looked up against the chart in the database</b>, not against the
/// <see cref="FinanceAccounts"/> constants. The constants name what a posting means; the rows are
/// what it posts to. That is the debt WP-0.8 recorded against this work package, and it is what
/// makes the foreign key on a journal line real.
/// </para>
/// </remarks>
public sealed class JournalPostingSeam(
    FinanceDbContext database,
    IJournalEntryNumberGenerator numbers,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider clock) : IJournalPostingSeam
{
    /// <inheritdoc />
    public Task PostAsync(JournalPostingIntent posting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(posting);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var accounts = await LoadAccountsAsync(posting, ct).ConfigureAwait(false);

                var entry = JournalEntry.Post(
                    await numbers.NextEntryNumberAsync(ct).ConfigureAwait(false),
                    posting,
                    accounts,
                    RegistryActor.Of(currentUser),
                    clock.GetUtcNow());

                database.JournalEntries.Add(entry);

                // INVARIANT 1. Attributed to `system`, because a posting happens in a consumer rather
                // than at somebody's keyboard — the clerk who took the payment or issued the bill is
                // named on that module's own entry, and the ledger's entry says which fact it acted
                // on. There is no before: an entry is a new fact, never an edit to an old one, and
                // the append-only guard in FinanceDbContext makes sure it stays that way.
                audit.Record(
                    AuditActions.JournalPosted,
                    AuditEntityTypes.JournalEntry,
                    entry.Id.ToString(),
                    before: null,
                    after: JournalEntrySnapshot.Of(entry));
            },
            cancellationToken);
    }

    /// <summary>
    /// The chart rows the posting names, keyed by code — one query for the whole entry rather than
    /// one per line.
    /// </summary>
    /// <remarks>
    /// Tracked rather than <c>AsNoTracking</c>: the lines hold a navigation to these accounts, and
    /// an untracked account attached to a tracked line is how EF is talked into trying to insert the
    /// chart of accounts a second time.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, Account>> LoadAccountsAsync(
        JournalPostingIntent posting,
        CancellationToken cancellationToken)
    {
        var codes = posting.Lines.Select(line => line.AccountCode).Distinct(StringComparer.Ordinal).ToList();

        var accounts = await database.Accounts
            .Where(account => codes.Contains(account.Code))
            .ToDictionaryAsync(account => account.Code, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        // Named here rather than left to JournalEntry.Post so the message says the chart was read
        // and the code was not in it — the difference between a mapping bug and a database that
        // never had the WP-0.8 migration applied.
        var missing = codes.Where(code => !accounts.ContainsKey(code)).ToList();

        if (missing.Count > 0)
        {
            throw new FinanceValidationException(
                $"The posting from {posting.Source} names {string.Join(", ", missing)}, which the chart of "
                + "accounts does not declare. Accounts are reference data shipped by migration; adding one "
                + "is a migration, never a runtime insert.");
        }

        return accounts;
    }
}

/// <summary>
/// A journal entry as the audit trail stores it — flat, and holding the figures somebody reading
/// the trail would otherwise have to go and fetch.
/// </summary>
/// <param name="Id">Identifier of the entry.</param>
/// <param name="EntryNumber">The number on the entry.</param>
/// <param name="EventId">The event that caused it.</param>
/// <param name="Source">Which upstream fact it came from.</param>
/// <param name="Reference">The business reference: a bill number, a provider reference.</param>
/// <param name="Currency">What the amounts are expressed in.</param>
/// <param name="PostedOn">The accounting date.</param>
/// <param name="ServiceAccountId">The service account it is about, where it is about one.</param>
/// <param name="CustomerId">The customer it is about, where it is about one.</param>
/// <param name="TotalDebits">Sum of the debits.</param>
/// <param name="TotalCredits">Sum of the credits. Equal to the debits, always.</param>
/// <param name="Lines">The accounts posted to and what each was moved by.</param>
public sealed record JournalEntrySnapshot(
    Guid Id,
    string EntryNumber,
    Guid? EventId,
    string Source,
    string Reference,
    string Currency,
    DateOnly PostedOn,
    Guid? ServiceAccountId,
    Guid? CustomerId,
    decimal TotalDebits,
    decimal TotalCredits,
    IReadOnlyList<JournalLineSnapshot> Lines)
{
    /// <summary>Takes a snapshot of <paramref name="entry"/> as it was posted.</summary>
    /// <remarks>
    /// The lines are on the snapshot, unlike a bill's on <c>BillSnapshot</c>. An audit trail of
    /// entries whose totals balance tells a reader nothing they did not already know — the question
    /// asked of a ledger is always which accounts moved.
    /// </remarks>
    public static JournalEntrySnapshot Of(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new JournalEntrySnapshot(
            entry.Id,
            entry.EntryNumber,
            entry.EventId,
            entry.Source,
            entry.Reference,
            entry.Currency,
            entry.PostedOn,
            entry.ServiceAccountId,
            entry.CustomerId,
            entry.TotalDebits,
            entry.TotalCredits,
            [.. entry.Lines.Select(JournalLineSnapshot.Of)]);
    }
}

/// <summary>One line of a journal entry, as the audit trail stores it.</summary>
/// <param name="AccountCode">The account posted to.</param>
/// <param name="AccountName">What that account is called.</param>
/// <param name="Debit">Amount debited.</param>
/// <param name="Credit">Amount credited.</param>
public sealed record JournalLineSnapshot(string AccountCode, string AccountName, decimal Debit, decimal Credit)
{
    /// <summary>Takes a snapshot of <paramref name="line"/>.</summary>
    public static JournalLineSnapshot Of(JournalLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new JournalLineSnapshot(line.Account.Code, line.Account.Name, line.Debit, line.Credit);
    }
}
