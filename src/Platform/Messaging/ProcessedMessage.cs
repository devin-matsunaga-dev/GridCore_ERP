namespace GridCore.Platform.Messaging;

/// <summary>
/// A record that one consumer has already handled one event. This table <i>is</i> the dedupe
/// helper's memory: invariant 2 of ARCHITECTURE.md requires every consumer to be idempotent, and a
/// broker that guarantees at-least-once delivery will redeliver.
/// </summary>
/// <remarks>
/// Keyed on (message, consumer) rather than on the message alone, because several modules may each
/// react to the same event and each must get exactly one turn.
/// </remarks>
public sealed class ProcessedMessage
{
    /// <summary>Longest consumer name the table stores.</summary>
    public const int ConsumerNameLength = 256;

    private ProcessedMessage()
    {
        // EF materialisation.
        Consumer = string.Empty;
    }

    /// <summary><see cref="Contracts.Events.IIntegrationEvent.EventId"/> of the handled event.</summary>
    public Guid MessageId { get; private init; }

    /// <summary>Stable name of the consumer that handled it.</summary>
    public string Consumer { get; private init; }

    /// <summary>When it was handled.</summary>
    public DateTimeOffset ProcessedAt { get; private init; }

    /// <summary>Builds a record of a handled message.</summary>
    /// <exception cref="ArgumentException">The message id is empty or the consumer name is blank.</exception>
    public static ProcessedMessage For(Guid messageId, string consumer, DateTimeOffset processedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);

        if (messageId == Guid.Empty)
        {
            throw new ArgumentException(
                "An event with no EventId cannot be deduplicated; publish events built by their For(...) factory.",
                nameof(messageId));
        }

        return new ProcessedMessage
        {
            MessageId = messageId,
            Consumer = consumer.Length > ConsumerNameLength ? consumer[..ConsumerNameLength] : consumer,
            ProcessedAt = processedAt,
        };
    }
}
