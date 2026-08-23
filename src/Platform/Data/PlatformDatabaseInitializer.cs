using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GridCore.Platform.Data;

/// <summary>
/// Applies pending platform migrations at startup when the host is configured to. On by default in
/// Development so <c>aspire run</c> against a fresh volume just works; off elsewhere, because a
/// production deploy applies migrations as its own step and never as a side effect of a pod
/// starting — WP-5.1 owns that step.
/// </summary>
public sealed partial class PlatformDatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<PlatformDatabaseInitializer> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        var database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var pending = (await database.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();

        if (pending.Count is 0)
        {
            return;
        }

        ApplyingMigrations(logger, pending.Count, string.Join(", ", pending));

        await database.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Information,
        Message = "Applying {Count} pending platform migration(s): {Migrations}")]
    private static partial void ApplyingMigrations(ILogger logger, int count, string migrations);
}
