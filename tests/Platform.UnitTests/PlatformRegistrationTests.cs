using GridCore.Platform.Approvals;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Notifications;
using GridCore.Platform.Scheduling;
using GridCore.Platform.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GridCore.Platform.UnitTests;

public class PlatformRegistrationTests
{
    private sealed class Environment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "GridCore.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

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
        services.AddGridCorePlatform(WithConnectionString(), new Environment(Environments.Development));

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
            () => services.AddGridCorePlatform(Configuration(), new Environment(Environments.Development)));

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

    private static ServiceCollection Initializers(string environmentName, string? applyOverride)
    {
        var settings = new List<(string, string?)>
        {
            ("ConnectionStrings:gridcore", "Host=localhost;Database=gridcore;Username=u;Password=p"),
        };

        if (applyOverride is not null)
        {
            settings.Add(("Platform:ApplyMigrationsAtStartup", applyOverride));
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGridCorePlatform(Configuration([.. settings]), new Environment(environmentName));

        return services;
    }

    private static bool IsInitializer(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType == typeof(PlatformDatabaseInitializer);
}
