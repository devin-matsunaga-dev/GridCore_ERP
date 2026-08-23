using GridCore.Contracts.Events;
using MassTransit;

namespace GridCore.Platform.Messaging;

/// <summary>
/// Publishes through MassTransit's transactional outbox.
/// </summary>
/// <remarks>
/// With <c>UseBusOutbox()</c> configured, the scoped <see cref="IPublishEndpoint"/> does not talk
/// to RabbitMQ at all: it adds a row to <c>platform.outbox_message</c> through
/// <see cref="Data.PlatformDbContext"/>. The row is saved by whatever saves that context — normally
/// <see cref="Data.IUnitOfWork"/> — and MassTransit's delivery service moves it to the broker after
/// the commit. Publishing without then saving the platform context therefore publishes nothing,
/// which is the intended failure mode: no event without its committed cause.
/// </remarks>
public sealed class OutboxEventPublisher(IPublishEndpoint publishEndpoint) : IEventPublisher
{
    /// <inheritdoc />
    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : class, IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (@event.EventId == Guid.Empty)
        {
            throw new ArgumentException(
                $"{typeof(TEvent).Name} carries no EventId, so no consumer could deduplicate it; build events with their For(...) factory.",
                nameof(@event));
        }

        return publishEndpoint.Publish(@event, cancellationToken);
    }
}
