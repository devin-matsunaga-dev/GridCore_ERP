namespace GridCore.Platform.Messaging;

/// <summary>
/// The dedupe helper invariant 2 of ARCHITECTURE.md refers to: consumers are idempotent, and this
/// is how they become idempotent without each one inventing its own scheme.
/// </summary>
public interface IMessageDeduplicator
{
    /// <summary>
    /// Claims an event for a consumer. Returns <see langword="true"/> the first time and
    /// <see langword="false"/> for every redelivery.
    /// </summary>
    /// <remarks>
    /// The claim is added to the current unit of work but not saved, so it commits with whatever
    /// the consumer does in response — a consumer that fails half-way is not marked as done.
    /// </remarks>
    /// <param name="messageId">The event's <see cref="Contracts.Events.IIntegrationEvent.EventId"/>.</param>
    /// <param name="consumer">Stable name of the consumer claiming it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The message id is empty or the consumer name is blank.</exception>
    Task<bool> TryBeginAsync(Guid messageId, string consumer, CancellationToken cancellationToken = default);
}
