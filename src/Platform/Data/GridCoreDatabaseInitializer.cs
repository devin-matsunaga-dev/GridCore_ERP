using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GridCore.Platform.Data;

/// <summary>
/// Applies pending migrations at startup when the host is configured to. On by default in
/// Development so <c>aspire run</c> against a fresh volume just works; off elsewhere, because a
/// production deploy applies migrations as its own step and never as a side effect of a pod
/// starting — WP-5.1 owns that step.
/// </summary>
/// <remarks>
/// Every context registered with <see cref="DataRegistration.AddGridCoreDbContext{TContext}"/> is
/// migrated, not just the platform's. Modules own their own schemas and their own migrations, so a
/// module added in a later WP is migrated the day it appears with nothing here to remember to
/// update — and a module whose schema had been left unmigrated would fail on its first query rather
/// than at startup.
/// </remarks>
public sealed partial class GridCoreDatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<GridCoreDatabaseInitializer> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        var contexts = scope.ServiceProvider
            .GetServices<UnitOfWorkParticipant>()
            .Select(participant => participant.Context);

        foreach (var context in contexts)
        {
            await MigrateAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task MigrateAsync(DbContext context, CancellationToken cancellationToken)
    {
        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();

        if (pending.Count is 0)
        {
            return;
        }

        ApplyingMigrations(logger, pending.Count, context.GetType().Name, string.Join(", ", pending));

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Information,
        Message = "Applying {Count} pending migration(s) for {Context}: {Migrations}")]
    private static partial void ApplyingMigrations(ILogger logger, int count, string context, string migrations);
}
