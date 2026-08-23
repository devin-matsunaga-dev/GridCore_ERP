namespace GridCore.Modules.Finance.Features.EventSeam;

/// <summary>
/// Where a posting goes once an event has been turned into one. WP-2.6 replaces the implementation
/// with the real general ledger; until then <see cref="LoggingJournalPostingSeam"/> records that it
/// would have posted, and the seam is swapped by DI exactly like the provider interfaces and the
/// notification stub.
/// </summary>
public interface IJournalPostingSeam
{
    /// <summary>
    /// Posts one journal entry. Called inside the consumer's unit of work, so an implementation
    /// adds to its context and never commits: the entry, the dedupe claim and everything else in
    /// the transaction land together or not at all.
    /// </summary>
    Task PostAsync(JournalPostingIntent posting, CancellationToken cancellationToken = default);
}
