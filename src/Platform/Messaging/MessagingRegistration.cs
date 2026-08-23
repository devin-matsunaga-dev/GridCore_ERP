using GridCore.Platform.Data;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GridCore.Platform.Messaging;

/// <summary>Options for the bus, bound from the <c>Messaging</c> section.</summary>
public sealed class GridCoreMessagingOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Messaging";

    /// <summary>Name of the AppHost-supplied RabbitMQ connection string.</summary>
    public string ConnectionStringName { get; set; } = "rabbitmq";

    /// <summary>How often the delivery service sweeps the outbox for committed rows.</summary>
    public TimeSpan OutboxQueryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a handled-message claim is kept before it can be pruned.</summary>
    public TimeSpan DuplicateDetectionWindow { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Retry intervals applied to a failing consumer before the message is faulted.</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>Wait between consumer retries.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// A consumer a module has asked the host to run. Modules register these from their
/// <c>AddServices</c>; <see cref="MessagingRegistration.AddGridCoreMessaging"/> reads them when it
/// configures the bus, which is why it is called after the modules in <c>Program.cs</c>.
/// </summary>
/// <param name="ConsumerType">The <see cref="IConsumer"/> implementation.</param>
public sealed record EventConsumerDescriptor(Type ConsumerType);

/// <summary>Host-side wiring for the bus, the transactional outbox and the dedupe helper.</summary>
public static class MessagingRegistration
{
    /// <summary>
    /// Registers a module's consumer. Modules never configure the bus themselves — they say what
    /// they consume, the host decides how messages arrive.
    /// </summary>
    public static IServiceCollection AddEventConsumer<TConsumer>(this IServiceCollection services)
        where TConsumer : class, IConsumer
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(new EventConsumerDescriptor(typeof(TConsumer)));

        return services;
    }

    /// <summary>
    /// Wires MassTransit over RabbitMQ with the EF transactional outbox in the platform schema.
    /// Call <b>after</b> the modules have been added, so their consumers are already registered.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configured connection string is missing.</exception>
    public static IServiceCollection AddGridCoreMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(GridCoreMessagingOptions.SectionName);
        var options = section.Get<GridCoreMessagingOptions>() ?? new GridCoreMessagingOptions();

        services.Configure<GridCoreMessagingOptions>(section);

        var connectionString = configuration.GetConnectionString(options.ConnectionStringName);

        // Fail fast and by name, exactly as the platform schema does: a host that boots without a
        // broker would accept writes and silently never deliver their events.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' is not configured. The Aspire AppHost supplies it; "
                + "set ConnectionStrings__" + options.ConnectionStringName + " to run the host on its own.");
        }

        var consumerTypes = CollectConsumers(services);

        services.AddMassTransit(bus =>
        {
            bus.SetKebabCaseEndpointNameFormatter();
            bus.AddConsumers(consumerTypes);

            bus.AddEntityFrameworkOutbox<PlatformDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.QueryDelay = options.OutboxQueryDelay;
                outbox.DuplicateDetectionWindow = options.DuplicateDetectionWindow;

                // The half that matters: IPublishEndpoint writes to platform.outbox_message inside
                // the caller's transaction instead of going straight to the broker.
                outbox.UseBusOutbox();
            });

            bus.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.Host(new Uri(connectionString));
                rabbit.UseMessageRetry(retry => retry.Interval(options.RetryCount, options.RetryInterval));
                rabbit.ConfigureEndpoints(context);
            });
        });

        // The dedupe helper and the idempotent handler are plain platform services with no bus
        // dependency, registered by AddGridCorePlatform; only the publisher needs MassTransit.
        services.TryAddScoped<IEventPublisher, OutboxEventPublisher>();

        return services;
    }

    /// <summary>
    /// The consumer types modules registered, read back off the service collection — the same
    /// trick <c>AddScheduledJob</c> uses, so the composition stays greppable and nothing is scanned.
    /// </summary>
    internal static Type[] CollectConsumers(IServiceCollection services) =>
        [.. services
            .Where(descriptor => descriptor.ServiceType == typeof(EventConsumerDescriptor))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<EventConsumerDescriptor>()
            .Select(descriptor => descriptor.ConsumerType)
            .Distinct()];
}
