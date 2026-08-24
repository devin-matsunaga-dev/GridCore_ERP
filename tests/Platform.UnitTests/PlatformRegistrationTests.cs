using GridCore.Platform.Approvals;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Notifications;
using GridCore.Platform.Scheduling;
using GridCore.Platform.Security;
using GridCore.Platform.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GridCore.Platform.UnitTests;

public class PlatformRegistrationTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

    private static IConfiguration WithConnectionString() =>
        Configuration(("ConnectionStrings:gridcore", "Host=localhost;Database=gridcore;Username=u;Password=p"));

    private sealed class SweepJob : IScheduledJob
    {
        public string Name => "sweep";

        public TimeSpan Interval => TimeSpan.FromMinutes(15);

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void The_platform_services_are_resolvable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGridCorePlatform(WithConnectionString(), new FakeHostEnvironment(Environments.Development));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<AuditLog>(scope.ServiceProvider.GetRequiredService<IAuditLog>());
        Assert.IsType<ApprovalService>(scope.ServiceProvider.GetRequiredService<IApprovalService>());
        Assert.IsType<HttpContextCurrentUser>(scope.ServiceProvider.GetRequiredService<ICurrentUser>());
        Assert.IsType<LoggingNotificationSender>(scope.ServiceProvider.GetRequiredService<INotificationSender>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PlatformDbContext>());
    }

    [Fact]
    public void A_missing_connection_string_stops_the_host_by_name_rather_than_500ing_later()
    {
        var services = new ServiceCollection();

        var refused = Assert.Throws<InvalidOperationException>(
            () => services.AddGridCorePlatform(Configuration(), new FakeHostEnvironment(Environments.Development)));

        Assert.Contains("gridcore", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Migrations_are_applied_at_startup_in_development_and_not_outside_it()
    {
        Assert.Contains(Initializers(Environments.Development, applyOverride: null), IsInitializer);
        Assert.DoesNotContain(Initializers(Environments.Production, applyOverride: null), IsInitializer);
    }

    [Fact]
    public void The_startup_migration_can_be_turned_on_or_off_by_configuration()
    {
        Assert.DoesNotContain(Initializers(Environments.Development, applyOverride: "false"), IsInitializer);
        Assert.Contains(Initializers(Environments.Production, applyOverride: "true"), IsInitializer);
    }

    [Fact]
    public void A_scheduled_job_is_registered_scoped_so_it_can_take_a_DbContext()
    {
        var services = new ServiceCollection();
        services.AddScheduledJob<SweepJob>();

        Assert.Equal(
            ServiceLifetime.Scoped,
            services.Single(descriptor => descriptor.ServiceType == typeof(SweepJob)).Lifetime);

        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            typeof(SweepJob),
            provider.GetRequiredService<ScheduledJobDescriptor>().JobType);
    }

    [Fact]
    public void The_demo_world_is_seeded_in_development_and_nowhere_else()
    {
        // ARCHITECTURE.md invariant 8 at the composition level: outside Development the runner is
        // not even registered, so there is nothing to switch on by accident.
        Assert.Contains(Platform(Environments.Development), IsSeedRunner);
        Assert.Contains(Platform(Environments.Development), IsDemoSeeder);

        Assert.DoesNotContain(Platform(Environments.Production), IsSeedRunner);
        Assert.DoesNotContain(Platform(Environments.Production), IsDemoSeeder);
    }

    [Fact]
    public void Development_seeding_can_be_turned_off_by_configuration_but_production_cannot_be_turned_on()
    {
        Assert.DoesNotContain(
            Platform(Environments.Development, ("Platform:SeedDemoData", "false")),
            IsSeedRunner);

        // The failure path: the setting narrows what the environment permits and never widens it.
        Assert.DoesNotContain(
            Platform(Environments.Production, ("Platform:SeedDemoData", "true")),
            IsSeedRunner);
    }

    [Fact]
    public void The_seed_runner_starts_after_the_migration_initializer()
    {
        // Hosted services start in registration order, and a seeder writing to a schema that has
        // not been migrated yet fails on exactly the fresh volume seeding exists for.
        var services = Platform(Environments.Development);

        var initializer = services.ToList().FindIndex(IsInitializer);
        var seeder = services.ToList().FindIndex(IsSeedRunner);

        Assert.InRange(initializer, 0, seeder - 1);
    }

    private static ServiceCollection Platform(string environmentName, params (string Key, string? Value)[] settings)
    {
        var configured = new List<(string, string?)>
        {
            ("ConnectionStrings:gridcore", "Host=localhost;Database=gridcore;Username=u;Password=p"),
        };

        configured.AddRange(settings.Select(setting => (setting.Key, setting.Value)));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGridCorePlatform(Configuration([.. configured]), new FakeHostEnvironment(environmentName));

        return services;
    }

    private static ServiceCollection Initializers(string environmentName, string? applyOverride) =>
        applyOverride is null
            ? Platform(environmentName)
            : Platform(environmentName, ("Platform:ApplyMigrationsAtStartup", applyOverride));

    private static bool IsInitializer(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType == typeof(GridCoreDatabaseInitializer);

    private static bool IsSeedRunner(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType == typeof(DemoSeedRunner);

    private static bool IsDemoSeeder(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IDemoSeeder)
        && descriptor.ImplementationType == typeof(ApprovalQueueDemoSeeder);
}
