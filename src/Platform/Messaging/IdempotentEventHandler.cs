using GridCore.Platform.Data;
using Microsoft.Extensions.Logging;

namespace GridCore.Platform.Messaging;

/// <summary>
/// Runs a consumer's work exactly once per event, in one transaction with the claim that says it
/// ran. Deliberately free of MassTransit types so the whole consume path can be unit tested in the
/// fast tier without a bus or a broker — <see cref="IdempotentConsumer{TEvent}"/> is the thin
/// adapter that connects it to the transport.
/// </summary>
public sealed partial class IdempotentEventHandler(
    IUnitOfWork unitOfWork,
    IMessageDeduplicator deduplicator,
    ILogger<IdempotentEventHandler> logger)
{
    /// <summary>
    /// Claims <paramref name="messageId"/> for <paramref name="consumer"/> and, if the claim is
    /// new, runs <paramref name="handle"/>. The claim and everything the handler wrote commit
    /// together: a handler that throws leaves no claim behind, so the redelivery gets a real
    /// second attempt rather than being skipped as already done.
    /// </summary>
    /// <returns><see langword="true"/> if the handler ran; <see langword="false"/> for a redelivery.</returns>
    /// <exception cref="ArgumentException">The message id is empty or the consumer name is blank.</exception>
    public Task<bool> HandleAsync(
        Guid messageId,
        string consumer,
        Func<CancellationToken, Task> handle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        return unitOfWork.ExecuteAsync(
            async token =>
            {
                if (!await deduplicator.TryBeginAsync(messageId, consumer, token).ConfigureAwait(false))
                {
                    DuplicateSkipped(logger, consumer, messageId);

                    return false;
                }

                await handle(token).ConfigureAwait(false);

                return true;
            },
            cancellationToken);
    }

    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Debug,
        Message = "Consumer {Consumer} has already handled event {EventId}; skipping the redelivery.")]
    private static partial void DuplicateSkipped(ILogger logger, string consumer, Guid eventId);
}
