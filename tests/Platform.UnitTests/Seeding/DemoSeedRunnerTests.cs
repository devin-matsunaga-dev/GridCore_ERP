using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Seeding;
using GridCore.Platform.UnitTests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GridCore.Platform.UnitTests.Seeding;

/// <summary>
/// The seeding machinery against a real database — SQLite in-memory, per CONVENTIONS.md rule C.
/// What is under test is the guard, idempotency and atomicity: all three need a database and
/// emphatically not a container.
/// </summary>
public class DemoSeedRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 30, 0, TimeSpan.Zero);

    /// <summary>Writes a row into the pretend module's schema, as a real module seeder would.</summary>
    private sealed class ModuleSeeder(ModuleTestDbContext database) : IDemoSeeder
    {
        public string Name => "test.module";

        public int Order => 200;

        public Task SeedAsync(CancellationToken cancellationToken)
        {
            database.Rows.Add(new ModuleRow { Id = Guid.CreateVersion7(), Name = "seeded" });

            return Task.CompletedTask;
        }
    }

    /// <summary>Writes into the module schema and then fails, to prove nothing is left behind.</summary>
    private sealed class FailingSeeder(ModuleTestDbContext database) : IDemoSeeder
    {
        public string Name => "test.failing";

        public int Order => 100;

        public Task SeedAsync(CancellationToken cancellationToken)
        {
            database.Rows.Add(new ModuleRow { Id = Guid.CreateVersion7(), Name = "half written" });

            throw new InvalidOperationException("The demo dataset is inconsistent.");
        }
    }

    /// <summary>Records the order seeders were run in.</summary>
    private sealed class RecordingSeeder(string name, int order, List<string> ran) : IDemoSeeder
    {
        public string Name => name;

        public int Order => order;

        public Task SeedAsync(CancellationToken cancellationToken)
        {
            ran.Add(name);

            return Task.CompletedTask;
        }
    }

    private static DemoSeedRunner Runner(PlatformTestHost host, string? environmentName = null) =>
        new(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            new FakeHostEnvironment(environmentName ?? Environments.Development),
            new FakeClock(Now),
            NullLogger<DemoSeedRunner>.Instance);

    [Fact]
    public async Task Seeding_outside_development_throws_and_writes_nothing()
    {
        // The failure path invariant 8 exists for: the runner refuses even when something has
        // registered it, because "which environment is this" is the only answer that may decide.
        using var host = new PlatformTestHost(
            new FakeClock(Now),
            configure: services => services.AddDemoSeeder<ModuleSeeder>());

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Runner(host, Environments.Production).StartAsync(CancellationToken.None));

        Assert.Contains(Environments.Production, thrown.Message, StringComparison.Ordinal);

        await using var platform = host.NewPlatformContext();
        await using var module = host.NewModuleContext();

        Assert.Empty(await platform.DemoSeedRecords.ToListAsync());
        Assert.Empty(await module.Rows.ToListAsync());
    }

    [Fact]
    public async Task A_seeder_writes_its_data_and_is_recorded_as_having_run()
    {
        using var host = new PlatformTestHost(
            new FakeClock(Now),
            configure: services => services.AddDemoSeeder<ModuleSeeder>());

        await Runner(host).StartAsync(CancellationToken.None);

        await using var platform = host.NewPlatformContext();
        await using var module = host.NewModuleContext();

        var record = Assert.Single(await platform.DemoSeedRecords.ToListAsync());

        Assert.Equal("test.module", record.Name);
        Assert.Equal(Now, record.SeededAt);
        Assert.Single(await module.Rows.ToListAsync());
    }

    [Fact]
    public async Task Seeding_twice_seeds_once()
    {
        // Starting the host is not a rare event. Without the record, every restart would deal
        // another demo world into the same database.
        using var host = new PlatformTestHost(
            new FakeClock(Now),
            configure: services => services.AddDemoSeeder<ModuleSeeder>());

        await Runner(host).StartAsync(CancellationToken.None);
        await Runner(host).StartAsync(CancellationToken.None);

        await using var platform = host.NewPlatformContext();
        await using var module = host.NewModuleContext();

        Assert.Single(await platform.DemoSeedRecords.ToListAsync());
        Assert.Single(await module.Rows.ToListAsync());
    }

    [Fact]
    public async Task A_seeder_that_throws_leaves_neither_its_rows_nor_its_record()
    {
        // Both halves commit or neither does, so the next start genuinely retries rather than
        // skipping a half-written demo world as done.
        using var host = new PlatformTestHost(
            new FakeClock(Now),
            configure: services => services.AddDemoSeeder<FailingSeeder>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Runner(host).StartAsync(CancellationToken.None));

        await using var platform = host.NewPlatformContext();
        await using var module = host.NewModuleContext();

        Assert.Empty(await platform.DemoSeedRecords.ToListAsync());
        Assert.Empty(await module.Rows.ToListAsync());
    }

    [Fact]
    public async Task Seeding_is_audited_in_the_same_transaction_as_the_data()
    {
        using var host = new PlatformTestHost(
            new FakeClock(Now),
            configure: services => services.AddDemoSeeder<ModuleSeeder>());

        await Runner(host).StartAsync(CancellationToken.None);

        await using var platform = host.NewPlatformContext();

        var entry = Assert.Single(await platform.AuditEntries.ToListAsync());

        Assert.Equal(AuditActions.DemoSeeded, entry.Action);
        Assert.Equal(AuditEntityTypes.DemoSeedRecord, entry.EntityType);
        Assert.Equal("test.module", entry.EntityId);
    }

    [Fact]
    public async Task Seeders_run_in_declared_order_lowest_first()
    {
        // Modules seed in dependency order — a work order needs its asset — so the order a module
        // declares is the order it gets, not the order the container happened to resolve them in.
        var ran = new List<string>();

        using var host = new PlatformTestHost(
            new FakeClock(Now),
            configure: services =>
            {
                services.AddScoped<IDemoSeeder>(_ => new RecordingSeeder("third", 300, ran));
                services.AddScoped<IDemoSeeder>(_ => new RecordingSeeder("first", 100, ran));
                services.AddScoped<IDemoSeeder>(_ => new RecordingSeeder("second", 200, ran));
            });

        await Runner(host).StartAsync(CancellationToken.None);

        Assert.Equal(["first", "second", "third"], ran);
    }

    [Fact]
    public async Task A_host_with_no_seeders_writes_nothing()
    {
        using var host = new PlatformTestHost(new FakeClock(Now));

        await Runner(host).StartAsync(CancellationToken.None);

        await using var platform = host.NewPlatformContext();

        Assert.Empty(await platform.DemoSeedRecords.ToListAsync());
    }
}
