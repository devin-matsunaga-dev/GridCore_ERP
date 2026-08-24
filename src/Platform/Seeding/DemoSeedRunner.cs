using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GridCore.Platform.Seeding;

/// <summary>
/// Runs every registered <see cref="IDemoSeeder"/> once, at startup, in Development only.
/// </summary>
/// <remarks>
/// Registered after <see cref="GridCoreDatabaseInitializer"/> because hosted services start in
/// registration order, and a seeder writing to a schema that has not been migrated yet would fail on
/// a fresh volume — precisely the case seeding exists for.
/// </remarks>
public sealed partial class DemoSeedRunner(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    TimeProvider clock,
    ILogger<DemoSeedRunner> logger) : IHostedService
{
    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The host is not running in Development.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        DemoSeedGuard.EnsureAllowed(environment);

        using var scope = scopeFactory.CreateScope();

        var seeders = scope.ServiceProvider
            .GetServices<IDemoSeeder>()
            .OrderBy(seeder => seeder.Order)
            .ThenBy(seeder => seeder.Name, StringComparer.Ordinal)
            .ToList();

        if (seeders.Count is 0)
        {
            return;
        }

        var database = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();

        var alreadySeeded = await database.DemoSeedRecords
            .Select(record => record.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var seeder in seeders.Where(seeder => !alreadySeeded.Contains(seeder.Name, StringComparer.Ordinal)))
        {
            await RunAsync(seeder, database, unitOfWork, audit, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Seeds one module's demo data and records the run in the same transaction. Both halves commit
    /// or neither does: a seeder that throws leaves no record, so the next start genuinely retries
    /// rather than skipping a half-written demo world.
    /// </summary>
    private async Task RunAsync(
        IDemoSeeder seeder,
        PlatformDbContext database,
        IUnitOfWork unitOfWork,
        IAuditLog audit,
        CancellationToken cancellationToken)
    {
        Seeding(logger, seeder.Name);

        await unitOfWork.ExecuteAsync(
            async ct =>
            {
                await seeder.SeedAsync(ct).ConfigureAwait(false);

                var record = DemoSeedRecord.For(seeder.Name, clock.GetUtcNow());

                database.DemoSeedRecords.Add(record);

                audit.Record(AuditActions.DemoSeeded, AuditEntityTypes.DemoSeedRecord, record.Name, after: record);
            },
            cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 4301,
        Level = LogLevel.Information,
        Message = "Seeding demo data: {Seeder}")]
    private static partial void Seeding(ILogger logger, string seeder);
}
