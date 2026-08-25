using Microsoft.Extensions.Logging;

namespace GridCore.Modules.Finance.Features.EventSeam;

/// <summary>
/// The no-op ledger: logs the balanced entry it would post rather than writing one.
/// </summary>
/// <remarks>
/// This is what made WP-0.5's seam demonstrable — an event published by Billing arrives at Finance,
/// becomes a balanced journal entry and is logged with its totals — while the general ledger itself
/// waited for WP-2.6. <see cref="JournalPostingSeam"/> is now what the module registers, and this
/// is kept rather than deleted: it is still the right implementation wherever the seam should be
/// visible without a finance schema behind it, and it is the shape a test double takes.
/// </remarks>
public sealed partial class LoggingJournalPostingSeam(ILogger<LoggingJournalPostingSeam> logger) : IJournalPostingSeam
{
    /// <inheritdoc />
    public Task PostAsync(JournalPostingIntent posting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(posting);

        WouldPost(
            logger,
            posting.Source,
            posting.Reference,
            posting.TotalDebits,
            posting.TotalCredits,
            posting.Currency,
            posting.Lines.Count,
            posting.EventId);

        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 4301,
        Level = LogLevel.Information,
        Message = "Finance would post {Source} {Reference}: debits {TotalDebits} = credits {TotalCredits} {Currency} over {LineCount} lines (event {EventId}).")]
    private static partial void WouldPost(
        ILogger logger,
        string source,
        string reference,
        decimal totalDebits,
        decimal totalCredits,
        string currency,
        int lineCount,
        Guid eventId);
}
