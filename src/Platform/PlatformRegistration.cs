using GridCore.Contracts.Providers;
using GridCore.Platform.Approvals;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Documents;
using GridCore.Platform.Messaging;
using GridCore.Platform.Notifications;
using GridCore.Platform.Scheduling;
using GridCore.Platform.Seeding;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GridCore.Platform;

/// <summary>Options for the cross-cutting platform services, bound from the <c>Platform</c> section.</summary>
public sealed class GridCorePlatformOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Platform";

    /// <summary>Name of the AppHost-supplied connection string the platform schema lives in.</summary>
    public string ConnectionStringName { get; set; } = "gridcore";

    /// <summary>
    /// Whether to apply pending migrations at startup. Defaults to on in Development and off
    /// elsewhere; a production deploy migrates as its own step.
    /// </summary>
    public bool? ApplyMigrationsAtStartup { get; set; }

    /// <summary>
    /// Whether to seed the demo world at startup. Defaults to on in Development and is ignored
    /// everywhere else — see <see cref="DemoSeedGuard"/>: this setting can only turn seeding off.
    /// </summary>
    public bool? SeedDemoData { get; set; }
}

/// <summary>Host-side wiring for audit, approvals, notifications and the scheduler.</summary>
public static class PlatformRegistration
{
    /// <summary>
    /// Registers the platform schema and the cross-cutting services every module depends on.
    /// Call after <see cref="SecurityRegistration.AddGridCoreSecurity"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configured connection string is missing.</exception>
    public static IServiceCollection AddGridCorePlatform(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var section = configuration.GetSection(GridCorePlatformOptions.SectionName);
        var options = section.Get<GridCorePlatformOptions>() ?? new GridCorePlatformOptions();

        services.Configure<GridCorePlatformOptions>(section);

        var connectionString = configuration.GetConnectionString(options.ConnectionStringName);

        // Fail fast and by name: the AppHost supplies this, and a host that starts without it would
        // 500 on the first audited write instead of refusing to boot.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' is not configured. The Aspire AppHost supplies it; "
                + "set ConnectionStrings__" + options.ConnectionStringName + " to run the host on its own.");
        }

        // One connection per scope, shared by every module's context, so a write in a module's
        // schema and the audit entry and outbox row in the platform's commit in one transaction.
        services.AddGridCoreDataAccess(_ => new GridCoreDbConnection(new NpgsqlConnection(connectionString)));

        services.AddGridCoreDbContext<PlatformDbContext>((builder, connection) =>
            builder.UseNpgsql(connection, GridCoreDbContexts.InSchema(PlatformDbContext.SchemaName)));

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ICurrentUser, HttpContextCurrentUser>();
        services.TryAddScoped<IAuditLog, AuditLog>();
        services.TryAddScoped<IApprovalService, ApprovalService>();
        services.TryAddScoped<IMessageDeduplicator, MessageDeduplicator>();
        services.TryAddScoped<IdempotentEventHandler>();
        services.TryAddSingleton<INotificationSender, LoggingNotificationSender>();
        services.AddHostedService<ScheduledJobRunner>();

        // The object store (WP-2.18). A singleton because the client owns an HttpClient, and
        // registered against the Contracts seam rather than the concrete type: a module uploads a
        // scanned lease without learning that MinIO exists, which is invariant 6 applied to storage.
        // The factory runs on first resolve rather than at boot — a host that never touches a
        // document never needs an object store — and the constructor validates the options, so a
        // missing endpoint is a named sentence at that point rather than a null-reference later.
        services.Configure<MinioDocumentStoreOptions>(configuration.GetSection(MinioDocumentStoreOptions.SectionName));
        services.TryAddSingleton<IDocumentStore>(provider =>
            new MinioDocumentStore(provider.GetRequiredService<IOptions<MinioDocumentStoreOptions>>().Value));

        if (options.ApplyMigrationsAtStartup ?? environment.IsDevelopment())
        {
            services.AddHostedService<GridCoreDatabaseInitializer>();
        }

        // After the initializer, deliberately: hosted services start in registration order, and a
        // seeder that runs before its schema exists fails on exactly the fresh volume it is for.
        if (DemoSeedGuard.IsAllowed(environment, options.SeedDemoData))
        {
            services.AddDemoSeeder<ApprovalQueueDemoSeeder>();
            services.AddHostedService<DemoSeedRunner>();
        }

        return services;
    }

    /// <summary>
    /// Registers a module's demo seeder. Resolved from the seeding scope, so it may take the
    /// module's own <see cref="Microsoft.EntityFrameworkCore.DbContext"/> and services.
    /// </summary>
    /// <remarks>
    /// Registering a seeder does not make it run: <see cref="DemoSeedRunner"/> is only registered
    /// where <see cref="DemoSeedGuard"/> permits it, so a module may call this unconditionally.
    /// </remarks>
    public static IServiceCollection AddDemoSeeder<TSeeder>(this IServiceCollection services)
        where TSeeder : class, IDemoSeeder
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IDemoSeeder, TSeeder>();

        return services;
    }

    /// <summary>
    /// Registers a recurring background job. The job is resolved from a fresh scope for each run,
    /// so it may take scoped dependencies such as a DbContext.
    /// </summary>
    public static IServiceCollection AddScheduledJob<TJob>(this IServiceCollection services)
        where TJob : class, IScheduledJob
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<TJob>();
        services.AddSingleton(new ScheduledJobDescriptor(typeof(TJob)));

        return services;
    }

    /// <summary>Maps the platform's own endpoints: the audit trail and the approval queue.</summary>
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapMeEndpoints();
        endpoints.MapAuditEndpoints();
        endpoints.MapApprovalEndpoints();

        return endpoints;
    }
}
