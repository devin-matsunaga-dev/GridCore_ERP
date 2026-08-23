using GridCore.Platform.Approvals;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Notifications;
using GridCore.Platform.Scheduling;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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

        services.AddDbContext<PlatformDbContext>(builder => builder.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable(
                PlatformDbContext.MigrationsHistoryTable,
                PlatformDbContext.SchemaName)));

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ICurrentUser, HttpContextCurrentUser>();
        services.TryAddScoped<IAuditLog, AuditLog>();
        services.TryAddScoped<IApprovalService, ApprovalService>();
        services.TryAddSingleton<INotificationSender, LoggingNotificationSender>();
        services.AddHostedService<ScheduledJobRunner>();

        if (options.ApplyMigrationsAtStartup ?? environment.IsDevelopment())
        {
            services.AddHostedService<PlatformDatabaseInitializer>();
        }

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
