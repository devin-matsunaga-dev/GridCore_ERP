using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Documents;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Modules.Billing.Seeding;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GridCore.Modules.Billing.UnitTests.Infrastructure;

/// <summary>
/// The billing schema and the platform schema on one SQLite in-memory connection — the fast-tier
/// equivalent of the shared Postgres connection the host gives a request. That is what lets these
/// tests assert the thing that actually matters about issuing a bill: the bill row, its audit entry
/// and its <c>BillIssued</c> event all belong to one transaction (CONVENTIONS.md rule C).
/// </summary>
/// <remarks>
/// The customers and metering schemas are deliberately <b>absent</b>. Billing reads accounts through
/// <see cref="IServiceAccountDirectory"/> and readings through <see cref="IMeterReadingDirectory"/>,
/// so the two fakes stand in for two whole modules and their databases — which is the point of the
/// seams, and the reason a billing run can be tested in milliseconds.
/// </remarks>
public sealed class BillingTestHost : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    /// <param name="clock">The clock the host uses; a <see cref="FakeClock"/> keeps tests off wall time.</param>
    /// <param name="currentUser">Who is acting. Defaults to the system, as background work is.</param>
    public BillingTestHost(TimeProvider? clock = null, ICurrentUser? currentUser = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var services = new ServiceCollection();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddSingleton(currentUser ?? SystemUser.Instance);
        services.AddSingleton<IEventPublisher>(Events);
        services.AddSingleton<IServiceAccountDirectory>(Accounts);
        services.AddSingleton<IMeterReadingDirectory>(Readings);

        // ownsConnection: false — the in-memory database lives only as long as this connection, and
        // each scope disposing it would delete the database mid-test.
        services.AddGridCoreDataAccess(_ => new GridCoreDbConnection(_connection, ownsConnection: false));
        services.AddGridCoreDbContext<PlatformDbContext>((builder, connection) => builder.UseSqlite(connection));
        services.AddGridCoreDbContext<BillingDbContext>((builder, connection) => builder.UseSqlite(connection));

        services.AddScoped<IAuditLog, AuditLog>();

        // The consume path, as the platform registers it. Billing gained its first consumer in
        // WP-2.5, and what a test needs to prove about it — one balance change per approval, however
        // often the broker redelivers — is the real dedupe store over the real unit of work, not a
        // stub of either.
        services.AddScoped<IMessageDeduplicator, MessageDeduplicator>();
        services.AddScoped<IdempotentEventHandler>();

        services.AddScoped<IBillNumberGenerator, SequentialBillNumberGenerator>();
        services.AddScoped<IRatePlanService, RatePlanService>();
        services.AddScoped<IBillService, BillService>();

        // The reprint (WP-2.14). Registered like every other slice, so a test resolves it the way
        // the host does; the ones that prove a refusal build it by hand with another caller.
        services.AddScoped<IBillDocumentService, BillDocumentService>();
        services.AddScoped<BillsDemoSeeder>();

        _provider = services.BuildServiceProvider();

        CreateTables();
    }

    /// <summary>Everything the register published while the test ran.</summary>
    public RecordingEventPublisher Events { get; } = new();

    /// <summary>The service accounts the register is allowed to see.</summary>
    public FakeServiceAccountDirectory Accounts { get; } = new();

    /// <summary>The readings the register is allowed to bill.</summary>
    public FakeMeterReadingDirectory Readings { get; } = new();

    /// <summary>Runs <paramref name="work"/> in its own DI scope, as a request would.</summary>
    public async Task<TResult> InScopeAsync<TResult>(Func<IServiceProvider, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = _provider.CreateAsyncScope();

        return await work(scope.ServiceProvider);
    }

    /// <summary>Runs <paramref name="work"/> against the billing register, in its own scope.</summary>
    public Task<TResult> WithBillsAsync<TResult>(Func<IBillService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IBillService>()));
    }

    /// <summary>Runs <paramref name="work"/> against the bill reprint, in its own scope.</summary>
    public Task<TResult> WithDocumentsAsync<TResult>(Func<IBillDocumentService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IBillDocumentService>()));
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the reprint as <paramref name="caller"/>, over this
    /// host's database.
    /// </summary>
    /// <remarks>
    /// The one place a Billing test needs two identities against one dataset: proving a reprint is
    /// refused means somebody who <i>may</i> raise a bill has to have raised one first. Composed by
    /// hand rather than through the container, because the caller is the single dependency swapped.
    /// </remarks>
    public Task<TResult> AsAsync<TResult>(ICurrentUser caller, Func<IBillDocumentService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(new BillDocumentService(
            services.GetRequiredService<BillingDbContext>(),
            services.GetRequiredService<IAuditLog>(),
            caller,
            services.GetRequiredService<TimeProvider>())));
    }

    /// <summary>Runs <paramref name="work"/> against the tariff catalogue, in its own scope.</summary>
    public Task<TResult> WithTariffsAsync<TResult>(Func<IRatePlanService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IRatePlanService>()));
    }

    /// <summary>Reads back what a test wrote, on a context outside any unit of work.</summary>
    public BillingDbContext NewBillingContext() =>
        new(new DbContextOptionsBuilder<BillingDbContext>().UseSqlite(_connection).Options);

    /// <summary>Reads back the audit trail a register write produced.</summary>
    public PlatformDbContext NewPlatformContext() =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Creates both schemas. <c>EnsureCreated</c> cannot do this: it returns false once the
    /// database exists, so the second context's tables would silently never be created. It also
    /// emits the configuration's <c>HasData</c> inserts, which is how the shipped tariffs reach the
    /// fast tier without a migration.
    /// </summary>
    private void CreateTables()
    {
        using var scope = _provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>().CreateTables();

        scope.ServiceProvider.GetRequiredService<BillingDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>().CreateTables();
    }
}
