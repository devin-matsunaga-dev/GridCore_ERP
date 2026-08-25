namespace GridCore.Modules.Finance.Features.EventSeam;

/// <summary>
/// Where a posting goes once an event has been turned into one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JournalPostingSeam"/> is the general ledger and is what the module registers (WP-2.6).
/// <see cref="LoggingJournalPostingSeam"/> is the no-op WP-0.5 proved the wiring with, kept because
/// it is still the right implementation for a deployment that wants the event seam demonstrable
/// without a finance schema — and because it is the shape a test double takes.
/// </para>
/// <para>
/// The interface survives the ledger's arrival for the reason every provider interface exists: what
/// a fact means in double entry is Finance's business, and where the resulting entry lands is a DI
/// registration. Nothing upstream of this changed when the no-op became a ledger.
/// </para>
/// </remarks>
public interface IJournalPostingSeam
{
    /// <summary>
    /// Posts one journal entry. Called inside the consumer's unit of work, so an implementation
    /// adds to its context rather than committing on its own: the entry, the dedupe claim and
    /// everything else in the transaction land together or not at all.
    /// </summary>
    Task PostAsync(JournalPostingIntent posting, CancellationToken cancellationToken = default);
}
