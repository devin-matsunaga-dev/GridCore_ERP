using GridCore.IntegrationTests.Infrastructure;
using GridCore.Platform.Data;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GridCore.IntegrationTests;

/// <summary>
/// The demo seeder against real Postgres and the shipped composition. The fast tier proves the
/// guard, the ordering and the atomicity on SQLite; what a container adds is that the runner's
/// transaction spans the platform schema on a real connection, and that the shipped host actually
/// registers a seeder to run.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DemoSeedTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The runner as the host would start it. It is registered as a hosted service and therefore
    /// cannot be resolved, so it is constructed over the booted host's own scope factory — the same
    /// services, the same containers.
    /// </summary>
    private DemoSeedRunner Runner() =>
        new(
            fixture.Application.Services.GetRequiredService<IServiceScopeFactory>(),
            fixture.Application.Services.GetRequiredService<IHostEnvironment>(),
            fixture.Application.Services.GetRequiredService<TimeProvider>(),
            fixture.Application.Services.GetRequiredService<ILogger<DemoSeedRunner>>());

    [Fact]
    public async Task Seeding_fills_the_demo_approval_queue_and_records_the_run()
    {
        await Runner().StartAsync(CancellationToken.None);

        await using var scope = fixture.CreateScope();

        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var records = await platform.DemoSeedRecords.Select(record => record.Name).ToListAsync();

        // One record per seeder, and every registered seeder ran. Asserted as a set rather than a
        // count so a module contributing its own demo world extends this list instead of breaking it.
        Assert.Contains("platform.approval-queue", records);
        Assert.Equal(
            scope.ServiceProvider.GetServices<IDemoSeeder>().Select(seeder => seeder.Name).Order(),
            records.Order());

        Assert.Equal(2, await platform.ApprovalRequests.CountAsync());
    }

    [Fact]
    public async Task Seeding_a_database_that_is_already_seeded_changes_nothing()
    {
        // A host restart is not a rare event. Without the record, every one would deal another
        // demo world into the same database.
        await Runner().StartAsync(CancellationToken.None);
        await Runner().StartAsync(CancellationToken.None);

        await using var scope = fixture.CreateScope();

        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        // One record per seeder still, not two — and, the reason the record exists, no second copy
        // of anything a seeder wrote.
        Assert.Equal(
            scope.ServiceProvider.GetServices<IDemoSeeder>().Count(),
            await platform.DemoSeedRecords.CountAsync());

        Assert.Equal(2, await platform.ApprovalRequests.CountAsync());
    }
}
