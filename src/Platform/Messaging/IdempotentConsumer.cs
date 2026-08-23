using GridCore.Contracts.Events;
using MassTransit;

namespace GridCore.Platform.Messaging;

/// <summary>
/// Base class for every consumer in the system. Handles the transport, the transaction and the
/// deduplication, so a module's consumer only says what the event means to it.
/// </summary>
/// <remarks>
/// Consumers run outside any request, so <see cref="Security.ICurrentUser"/> resolves to
/// <see cref="Security.SystemUser"/> and anything the consumer writes is audited against
/// <c>system</c>.
/// </remarks>
/// <typeparam name="TEvent">The event consumed.</typeparam>
public abstract class IdempotentConsumer<TEvent>(IdempotentEventHandler handler) : IConsumer<TEvent>
    where TEvent : class, IIntegrationEvent
{
    /// <summary>
    /// Stable name identifying this consumer in <c>platform.processed_messages</c>. Never change
    /// it for a deployed consumer: a new name means every past event looks unhandled.
    /// </summary>
    protected abstract string ConsumerName { get; }

    /// <inheritdoc />
    public Task Consume(ConsumeContext<TEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return handler.HandleAsync(
            context.Message.EventId,
            ConsumerName,
            token => ConsumeAsync(context.Message, token),
            context.CancellationToken);
    }

    /// <summary>
    /// Reacts to the event. Runs at most once per event, inside the unit of work that also stores
    /// the claim — so it must not commit or save anything itself.
    /// </summary>
    protected abstract Task ConsumeAsync(TEvent message, CancellationToken cancellationToken);
}
