using GridCore.Contracts.Events;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Platform;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace GridCore.IntegrationTests.Messaging;

/// <summary>
/// One Postgres container and one RabbitMQ container for the whole messaging run, started once and
/// shared — per CONVENTIONS.md rule D, never one container per test class. WP-0.7 generalises this
/// into the shared collection fixture (with Respawn) the rest of the gate tier will use; WP-0.5
/// needs only what proves the outbox.
/// </summary>
public sealed class OutboxFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("gridcore")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();

    private IHost? _host;

    /// <summary>Records what the consumer actually received, so a test can await delivery.</summary>
    public DeliveryRecorder Recorder { get; } = new();

    /// <summary>The composed host: platform schema, bus, outbox and one consumer.</summary>
    public IHost Host => _host ?? throw new InvalidOperationException("The fixture has not been initialised.");

    /// <summary>A connection to the container, for reading the outbox table directly.</summary>
    public string PostgresConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:gridcore"] = _postgres.GetConnectionString(),
            ["ConnectionStrings:rabbitmq"] = _rabbit.GetConnectionString(),

            // The delivery sweep is the thing under test; do not make the test wait a second for it.
            ["Messaging:OutboxQueryDelay"] = "00:00:00.100",

            // Migrations are applied by the fixture below, not by a hosted service racing the test.
            ["Platform:ApplyMigrationsAtStartup"] = "false",
        });

        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddGridCorePlatform(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(Recorder);
        builder.Services.TryAddScoped<IJournalPostingSeam, RecordingJournalPostingSeam>();
        builder.Services.AddEventConsumer<BillIssuedConsumer>();
        builder.Services.AddGridCoreMessaging(builder.Configuration);

        _host = builder.Build();

        await MigrateAsync(_host);

        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await Task.WhenAll(_rabbit.DisposeAsync().AsTask(), _postgres.DisposeAsync().AsTask());
    }

    /// <summary>
    /// How many outbox rows carry this event, read on a connection of its own so only committed
    /// rows count. Targeted at one event rather than counting the table, so the assertion does not
    /// depend on what another test left behind.
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

    private static async Task MigrateAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
    }
}

/// <summary>Signals a test the moment a bill actually arrives at the consumer.</summary>
public sealed class DeliveryRecorder
{
    private readonly TaskCompletionSource<JournalPostingIntent> _delivered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with the posting Finance built from the delivered event.</summary>
    public Task<JournalPostingIntent> Delivered => _delivered.Task;

    /// <summary>How many postings arrived in total, for asserting that nothing arrived twice.</summary>
    public int Count => _count;

    private int _count;

    public void Record(JournalPostingIntent posting)
    {
        Interlocked.Increment(ref _count);

        _delivered.TrySetResult(posting);
    }
}

/// <summary>The no-op ledger, wired to the recorder so the gate suite can see the seam fire.</summary>
public sealed class RecordingJournalPostingSeam(DeliveryRecorder recorder) : IJournalPostingSeam
{
    public Task PostAsync(JournalPostingIntent posting, CancellationToken cancellationToken = default)
    {
        recorder.Record(posting);

        return Task.CompletedTask;
    }
}

/// <summary>Collection so the containers start once for the whole messaging run.</summary>
[CollectionDefinition(Name)]
public sealed class OutboxCollection : ICollectionFixture<OutboxFixture>
{
    public const string Name = "outbox";
}
