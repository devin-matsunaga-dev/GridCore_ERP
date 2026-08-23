using MassTransit;

namespace GridCore.Platform.UnitTests.Messaging;

/// <summary>
/// Captures what was published instead of publishing it. MassTransit's real endpoint writes to the
/// outbox table, which the gate-tier suite proves; here the only question is what
/// <see cref="GridCore.Platform.Messaging.OutboxEventPublisher"/> hands it, so the fast tier does
/// not start a bus.
/// </summary>
public sealed class RecordingPublishEndpoint : IPublishEndpoint
{
    /// <summary>Everything published, in order.</summary>
    public List<object> Published { get; } = [];

    public ConnectHandle ConnectPublishObserver(IPublishObserver observer) => throw new NotSupportedException();

    public Task Publish<T>(T message, CancellationToken cancellationToken = default)
        where T : class
    {
        Published.Add(message);

        return Task.CompletedTask;
    }

    public Task Publish<T>(T message, IPipe<PublishContext<T>> pipe, CancellationToken cancellationToken = default)
        where T : class => Publish(message, cancellationToken);

    public Task Publish<T>(T message, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default)
        where T : class => Publish(message, cancellationToken);

    public Task Publish(object message, CancellationToken cancellationToken = default) =>
        Publish<object>(message, cancellationToken);

    public Task Publish(object message, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default) =>
        Publish<object>(message, cancellationToken);

    public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default) =>
        Publish<object>(message, cancellationToken);

    public Task Publish(object message, Type messageType, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default) =>
        Publish<object>(message, cancellationToken);

    public Task Publish<T>(object values, CancellationToken cancellationToken = default)
        where T : class => throw new NotSupportedException();

    public Task Publish<T>(object values, IPipe<PublishContext<T>> pipe, CancellationToken cancellationToken = default)
        where T : class => throw new NotSupportedException();

    public Task Publish<T>(object values, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default)
        where T : class => throw new NotSupportedException();
}
