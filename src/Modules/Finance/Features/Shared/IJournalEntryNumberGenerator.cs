using GridCore.Modules.Finance.Data;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Finance.Features.Shared;

/// <summary>
/// The prefix this module's registry numbers are issued under. The <i>shape</i> of a number is the
/// platform's (<see cref="RegistryNumbers"/>); what letters a journal entry number carries is the
/// Finance module's own business.
/// </summary>
public static class JournalNumbers
{
    /// <summary>
    /// Prefix of a journal entry number, e.g. <c>JRN-000001</c>. Three letters, like <c>BIL-</c> and
    /// <c>PAY-</c>: an entry number is quoted in a reconciliation beside the bill number and the
    /// payment number it relates to, and three series that all looked alike would be three series
    /// somebody transposes.
    /// </summary>
    public const string JournalEntryNumberPrefix = "JRN-";
}

/// <summary>
/// Issues the next journal entry number. A seam, so the numbering scheme is one registration away
/// from changing — a utility migrating off a legacy ledger usually has to keep its own.
/// </summary>
public interface IJournalEntryNumberGenerator
{
    /// <summary>The next unused journal entry number.</summary>
    Task<string> NextEntryNumberAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Continues the journal series from the highest number already issued, inside the caller's
/// transaction.
/// </summary>
/// <remarks>
/// One <see cref="RegistryNumberSeries.NextAsync"/> over this module's own column; the race with a
/// concurrent posting and the ordering trade it depends on are documented there, because every
/// registry shares them. The unique index on <c>entry_number</c> is what makes the race safe: two
/// consumers posting at once cannot both keep <c>JRN-000007</c>, and the loser's message is
/// redelivered rather than its entry lost.
/// </remarks>
public sealed class SequentialJournalEntryNumberGenerator(FinanceDbContext database) : IJournalEntryNumberGenerator
{
    /// <inheritdoc />
    public Task<string> NextEntryNumberAsync(CancellationToken cancellationToken = default) =>
        RegistryNumberSeries.NextAsync(
            JournalNumbers.JournalEntryNumberPrefix,
            database.JournalEntries
                .Where(entry => entry.EntryNumber.StartsWith(JournalNumbers.JournalEntryNumberPrefix))
                .OrderByDescending(entry => entry.EntryNumber)
                .Select(entry => entry.EntryNumber),
            cancellationToken);
}
