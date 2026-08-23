using GridCore.Contracts.Events;
using GridCore.Platform.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Platform.UnitTests.Messaging;

/// <summary>
/// The composition seam: modules say what they consume, the host configures the bus. Nothing is
/// assembly-scanned, so what runs is greppable.
/// </summary>
public sealed class MessagingRegistrationTests
{
    [Fact]
    public void Collects_the_consumers_modules_registered()
    {
        var services = new ServiceCollection();

        services.AddEventConsumer<TestConsumer>();
        services.AddEventConsumer<OtherTestConsumer>();

        Assert.Equal(
            [typeof(TestConsumer), typeof(OtherTestConsumer)],
            MessagingRegistration.CollectConsumers(services));
    }

    [Fact]
    public void Registers_a_consumer_once_however_many_modules_ask_for_it()
    {
        var services = new ServiceCollection();

        services.AddEventConsumer<TestConsumer>();
        services.AddEventConsumer<TestConsumer>();

        Assert.Equal([typeof(TestConsumer)], MessagingRegistration.CollectConsumers(services));
    }

    [Fact]
    public void Refuses_to_start_without_a_broker()
    {
        var services = new ServiceCollection();

        // Failure path: a host that boots without a broker would accept writes and silently never
        // deliver their events, which is worse than not booting.
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            services.AddGridCoreMessaging(new ConfigurationBuilder().Build()));

        Assert.Contains("rabbitmq", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wires_the_publisher_and_the_consumers_when_a_broker_is_configured()
    {
        var services = new ServiceCollection();

        services.AddEventConsumer<TestConsumer>();
        services.AddGridCoreMessaging(Configuration("amqp://guest:guest@localhost:5672"));

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IEventPublisher));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IBusControl));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(TestConsumer));
    }

    [Fact]
    public void Reads_the_broker_from_the_configured_connection_string_name()
    {
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Messaging:ConnectionStringName"] = "bus",
                ["ConnectionStrings:bus"] = "amqp://guest:guest@localhost:5672",
            })
            .Build();

        services.AddGridCoreMessaging(configuration);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IEventPublisher));
    }

    private static IConfiguration Configuration(string rabbitConnectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:rabbitmq"] = rabbitConnectionString })
            .Build();

    private sealed class TestConsumer : IConsumer<BillIssued>
    {
        public Task Consume(ConsumeContext<BillIssued> context) => Task.CompletedTask;
    }

    private sealed class OtherTestConsumer : IConsumer<PaymentApproved>
    {
        public Task Consume(ConsumeContext<PaymentApproved> context) => Task.CompletedTask;
    }
}
