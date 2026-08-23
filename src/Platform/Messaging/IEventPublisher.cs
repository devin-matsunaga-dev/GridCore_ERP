using GridCore.Contracts.Events;

namespace GridCore.Platform.Messaging;

/// <summary>
/// How a module publishes a domain event. The only publish path there is — invariant 2 of
/// ARCHITECTURE.md says every publish goes through the outbox, so nothing anywhere else takes a
/// dependency on the broker or on MassTransit's types.
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Enlists an event for publication. The event is written to the outbox in the same
    /// transaction as the change that caused it and is handed to the broker only once that
    /// transaction commits — so an event is never published for work that was rolled back, and
    /// committed work never loses its event because the broker happened to be down.
    /// </summary>
    /// <exception cref="ArgumentException">The event carries no <see cref="IIntegrationEvent.EventId"/>.</exception>
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : class, IIntegrationEvent;
}
