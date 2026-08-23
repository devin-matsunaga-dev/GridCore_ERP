using GridCore.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Platform.Messaging;

/// <summary>
/// The dedupe helper over <c>platform.processed_messages</c>.
/// </summary>
/// <remarks>
/// The read-then-insert is deliberately not locked. Two deliveries racing on the same pair both
/// read nothing and both insert; the composite primary key rejects the loser, its transaction rolls
/// back, and the broker redelivers it — by which time the winner's row is committed and the retry
/// is correctly skipped. Losing that race costs one redelivery; taking a lock would cost every
/// message.
/// </remarks>
public sealed class MessageDeduplicator(PlatformDbContext database, TimeProvider clock) : IMessageDeduplicator
{
    /// <inheritdoc />
    public async Task<bool> TryBeginAsync(Guid messageId, string consumer, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);

        if (messageId == Guid.Empty)
        {
            throw new ArgumentException(
                "An event with no EventId cannot be deduplicated; publish events built by their For(...) factory.",
                nameof(messageId));
        }

        // Local first: a claim made earlier in this same unit of work has not been saved yet, so
        // the database would not see it.
        var claimedInThisUnitOfWork = database.ProcessedMessages.Local.Any(
            message => message.MessageId == messageId && string.Equals(message.Consumer, consumer, StringComparison.Ordinal));

        if (claimedInThisUnitOfWork)
        {
            return false;
        }

        var alreadyProcessed = await database.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(message => message.MessageId == messageId && message.Consumer == consumer, cancellationToken)
            .ConfigureAwait(false);

        if (alreadyProcessed)
        {
            return false;
        }

        database.ProcessedMessages.Add(ProcessedMessage.For(messageId, consumer, clock.GetUtcNow()));

        return true;
    }
}
