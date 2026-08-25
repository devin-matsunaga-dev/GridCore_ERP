using GridCore.Modules.Assets.Data;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Finance.Data;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Modules.Inventory.Data;
using GridCore.Modules.Metering.Data;
using GridCore.Modules.Payments.Data;
using GridCore.Platform.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Respawn.Graph;
using Respawn;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace GridCore.IntegrationTests.Infrastructure;

/// <summary>
/// The whole gate tier's infrastructure: <b>one</b> Postgres, <b>one</b> RabbitMQ and <b>one</b>
/// Redis container, started once, migrated once, and one booted host over them — CONVENTIONS.md
/// rule D. A container per test class is what made the previous project's suite take hours; tests
/// get their clean slate from <see cref="ResetAsync"/> (a millisecond truncate), never from a new
/// container or a recreated database.
/// </summary>
public sealed class GateFixture : IAsyncLifetime
{
    /// <summary>Pinned images — a gate run must not change infrastructure underneath itself.</summary>
    private const string PostgresImage = "postgres:18-alpine";
    private const string RabbitMqImage = "rabbitmq:4-alpine";
    private const string RedisImage = "redis:8-alpine";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresImage)
        .WithDatabase("gridcore")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder(RabbitMqImage).Build();

    private readonly RedisContainer _redis = new RedisBuilder(RedisImage).Build();

    private GridCoreApplication? _application;
    private Respawner? _respawner;

    /// <summary>Postings Finance's event seam has produced, for tests that assert on the seam.</summary>
    public JournalPostingRecorder Postings { get; } = new();

    /// <summary>The booted host: every module, the bus, the outbox and the security pipeline.</summary>
    public GridCoreApplication Application =>
        _application ?? throw new InvalidOperationException("The gate fixture has not been initialised.");

    /// <summary>Connection string of the shared Postgres container, for reading tables directly.</summary>
    public string PostgresConnectionString => _postgres.GetConnectionString();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync(), _redis.StartAsync());

        // Migrations run once, here, on a context of their own — before the host starts, so the
        // outbox delivery service never polls a table that does not exist yet.
        await MigrateAsync();

        _respawner = await CreateRespawnerAsync();

        ApplyHostConfiguration();

        _application = new GridCoreApplication(ConfigureTestServices);

        // WebApplicationFactory builds and starts the host lazily; force it here so a composition
        // failure is reported by the fixture rather than blamed on whichever test ran first.
        _ = _application.Services;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_application is not null)
        {
            await _application.DisposeAsync();
        }

        await Task.WhenAll(
            _redis.DisposeAsync().AsTask(),
            _rabbit.DisposeAsync().AsTask(),
            _postgres.DisposeAsync().AsTask());
    }

    /// <summary>
    /// Wipes application data so the next test starts from a known slate. Respawn truncates; it
    /// does not drop the schema or reapply migrations, which is what keeps a reset in the
    /// milliseconds rather than the seconds.
    /// </summary>
    public async Task ResetAsync()
    {
        var respawner = _respawner
            ?? throw new InvalidOperationException("The gate fixture has not been initialised.");

        await using var connection = new NpgsqlConnection(PostgresConnectionString);

        await connection.OpenAsync();

        await respawner.ResetAsync(connection);

        Postings.Clear();
    }

    /// <summary>A scope over the booted host, for resolving the services a test drives.</summary>
    public AsyncServiceScope CreateScope() => Application.Services.CreateAsyncScope();

    /// <summary>
    /// How many outbox rows carry this event, read on a connection of its own so only committed
    /// rows count. Targeted at one event rather than counting the table, so the assertion does not
    /// depend on what the delivery service happens to be doing.
    /// </summary>
    public async Task<long> CountOutboxMessagesForAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionString);

        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        // The MassTransit columns keep the library's PascalCase names, hence the quoting.
        command.CommandText = """select count(*) from platform.outbox_message where "Body" like @pattern""";
        command.Parameters.Add(new NpgsqlParameter("pattern", $"%{eventId}%"));

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>
    /// Points the host at the shared containers. These are environment variables, not in-memory
    /// configuration, for the reason <see cref="GridCoreApplication"/> documents: under minimal
    /// hosting <c>Program.cs</c> has already read its configuration by the time a test host's
    /// callbacks run. Setting them is process-global, which is safe here because the gate suite is
    /// one serialized collection and this fixture is the only thing that writes them.
    /// </summary>
    private void ApplyHostConfiguration()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",

            ["ConnectionStrings__gridcore"] = _postgres.GetConnectionString(),
            ["ConnectionStrings__rabbitmq"] = _rabbit.GetConnectionString(),
            ["ConnectionStrings__redis"] = _redis.GetConnectionString(),

            // The host refuses to start without these (WP-0.3). No test presents a real token — the
            // gate tier asserts that an anonymous caller is refused, and a refusal needs no
            // metadata fetch, so the authority never has to resolve.
            ["Authentication__Authority"] = "https://identity.invalid/realms/gridcore",
            ["Authentication__Audience"] = "gridcore-api",

            // The delivery sweep is frequently the thing under test; do not make a test wait a
            // second for it. Kept off the floor so a truncate is never fighting a poll for a lock.
            ["Messaging__OutboxQueryDelay"] = "00:00:00.250",

            // Applied once by the fixture above, not by a hosted service racing the tests. This is
            // the one Development default the gate tier turns back off.
            ["Platform__ApplyMigrationsAtStartup"] = "false",
        };

        foreach (var (key, value) in settings)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private void ConfigureTestServices(IServiceCollection services)
    {
        // Finance's ledger is still a no-op (WP-2.6 owns the real one); the gate tier swaps the
        // logging seam for a recording one by DI, exactly as production will swap in the ledger.
        services.AddSingleton(Postings);
        services.AddScoped<IJournalPostingSeam, RecordingJournalPostingSeam>();
    }

    /// <summary>
    /// Applies every schema's migrations before the host starts, so the outbox delivery service
    /// never polls a table that does not exist yet. Each context is created directly rather than
    /// resolved from the host, because the host is what these migrations are for.
    /// </summary>
    private async Task MigrateAsync()
    {
        foreach (var context in CreateContexts())
        {
            await using (context)
            {
                await context.Database.MigrateAsync();
            }
        }
    }

    /// <summary>
    /// One instance of every GridCore context, on the shared container. Adding a module's context
    /// here is the one thing a new persisted schema owes the gate tier.
    /// </summary>
    private IEnumerable<DbContext> CreateContexts()
    {
        yield return new PlatformDbContext(Options<PlatformDbContext>(PlatformDbContext.SchemaName));
        yield return new FinanceDbContext(Options<FinanceDbContext>(FinanceDbContext.SchemaName));
        yield return new BillingDbContext(Options<BillingDbContext>(BillingDbContext.SchemaName));
        yield return new CustomersDbContext(Options<CustomersDbContext>(CustomersDbContext.SchemaName));
        yield return new MeteringDbContext(Options<MeteringDbContext>(MeteringDbContext.SchemaName));
        yield return new InventoryDbContext(Options<InventoryDbContext>(InventoryDbContext.SchemaName));
        yield return new AssetsDbContext(Options<AssetsDbContext>(AssetsDbContext.SchemaName));
        yield return new PaymentsDbContext(Options<PaymentsDbContext>(PaymentsDbContext.SchemaName));
    }

    private DbContextOptions<TContext> Options<TContext>(string schema)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(PostgresConnectionString, GridCoreDbContexts.InSchema(schema))
            .Options;

    private async Task<Respawner> CreateRespawnerAsync()
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionString);

        await connection.OpenAsync();

        return await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            TablesToIgnore =
            [
                .. await MigrationHistoryTablesAsync(connection),
                .. MessagingTables,
                .. ReferenceDataTables(),
            ],
        });
    }

    /// <summary>
    /// Tables a long-lived background service owns rather than any test: MassTransit's outbox and
    /// inbox, and the consumer dedupe table. Truncating them between tests would take an exclusive
    /// lock on a table the delivery service is polling — a flake generator — and buys nothing,
    /// because every event carries a fresh Guid v7 id and tests isolate on that id, not on an
    /// empty table.
    /// </summary>
    private static Table[] MessagingTables =>
    [
        new(PlatformDbContext.SchemaName, "outbox_message"),
        new(PlatformDbContext.SchemaName, "outbox_state"),
        new(PlatformDbContext.SchemaName, "inbox_state"),
        new(PlatformDbContext.SchemaName, "processed_messages"),
    ];

    /// <summary>
    /// Tables whose rows a migration seeded — the chart of accounts, the rate plans, the warehouses.
    /// </summary>
    /// <remarks>
    /// Truncating these would delete reference data that only a migration puts back, and the
    /// migration has already been recorded as applied, so it never would: the second test in a run
    /// would find an empty chart of accounts. They are discovered from each model's own seed data
    /// rather than listed, so a module shipping reference data in a later WP is protected the day it
    /// appears, with nothing here to remember to update.
    /// </remarks>
    private IEnumerable<Table> ReferenceDataTables()
    {
        foreach (var context in CreateContexts())
        {
            using (context)
            {
                // The design-time model, not the runtime one: seed data is configuration EF strips
                // out of the read-optimised model a running context uses.
                var model = context.GetService<IDesignTimeModel>().Model;

                foreach (var entity in model.GetEntityTypes().Where(entity => entity.GetSeedData().Any()))
                {
                    yield return new Table(entity.GetSchema() ?? model.GetDefaultSchema()!, entity.GetTableName()!);
                }
            }
        }
    }

    /// <summary>
    /// Every module's migrations-history table, discovered rather than listed. Truncating one would
    /// tell EF the schema was never migrated; discovery means a module added in a later WP is
    /// protected the day its schema appears, with nothing here to remember to update.
    /// </summary>
    private static async Task<IReadOnlyList<Table>> MigrationHistoryTablesAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();

        command.CommandText =
            "select table_schema, table_name from information_schema.tables where table_name = @name";
        command.Parameters.Add(new NpgsqlParameter("name", PlatformDbContext.MigrationsHistoryTable));

        var tables = new List<Table>();

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            tables.Add(new Table(reader.GetString(0), reader.GetString(1)));
        }

        return tables;
    }
}

/// <summary>
/// One collection for the whole gate suite, so the containers start once for the entire run rather
/// than once per class. Combined with <c>parallelizeTestCollections: false</c> in
/// <c>xunit.runner.json</c>, it also means a Respawn reset can never wipe the database out from
/// under a test running alongside it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GateCollection : ICollectionFixture<GateFixture>
{
    /// <summary>Name every gate-tier test class passes to <c>[Collection]</c>.</summary>
    public const string Name = "gate";
}
