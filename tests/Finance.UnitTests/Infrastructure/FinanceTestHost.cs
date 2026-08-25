using GridCore.Modules.Finance.Data;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Modules.Finance.Features.Journal;
using GridCore.Modules.Finance.Features.Reports;
using GridCore.Modules.Finance.Features.Shared;
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

namespace GridCore.Modules.Finance.UnitTests.Infrastructure;

/// <summary>
/// The finance schema and the platform schema on one SQLite in-memory connection — the fast-tier
/// equivalent of the shared Postgres connection the host gives a consumer. That is what lets these
/// tests assert the thing that actually matters about posting to a ledger: the journal entry, its
/// audit entry and the dedupe claim that says the event was handled all belong to one transaction
/// (CONVENTIONS.md rule C).
/// </summary>
/// <remarks>
/// <para>
/// <b>No fakes for other modules, unlike every test host before this one.</b> Billing's host fakes
/// two directories and Payments' fakes two more, because those modules read across seams. Finance
/// reads nothing: everything an entry needs is on the event that caused it. There is nothing here
/// to stand in for, which is what "downstream of everyone" buys.
/// </para>
/// <para>
/// The dedupe store and the idempotent handler are the real ones, over the real unit of work — what
/// a test needs to prove about a ledger is that a redelivered event posts once, and a stub of
/// either would prove it about the stub. Copied from <c>BillingTestHost</c>, which learned it in
/// WP-2.5.
/// </para>
/// </remarks>
public sealed class FinanceTestHost : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    /// <param name="clock">The clock the host uses; a <see cref="FakeClock"/> keeps tests off wall time.</param>
    /// <param name="currentUser">Who is acting. Defaults to the system, as a consumer is.</param>
    public FinanceTestHost(TimeProvider? clock = null, ICurrentUser? currentUser = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var services = new ServiceCollection();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddSingleton(currentUser ?? SystemUser.Instance);

        // ownsConnection: false — the in-memory database lives only as long as this connection, and
        // each scope disposing it would delete the database mid-test.
        services.AddGridCoreDataAccess(_ => new GridCoreDbConnection(_connection, ownsConnection: false));
        services.AddGridCoreDbContext<PlatformDbContext>((builder, connection) => builder.UseSqlite(connection));
        services.AddGridCoreDbContext<FinanceDbContext>((builder, connection) => builder.UseSqlite(connection));

        services.AddScoped<IAuditLog, AuditLog>();

        // The consume path, as the platform registers it.
        services.AddScoped<IMessageDeduplicator, MessageDeduplicator>();
        services.AddScoped<IdempotentEventHandler>();

        services.AddScoped<IJournalEntryNumberGenerator, SequentialJournalEntryNumberGenerator>();
        services.AddScoped<IJournalPostingSeam, JournalPostingSeam>();
        services.AddScoped<IChartOfAccountsService, ChartOfAccountsService>();
        services.AddScoped<IJournalService, JournalService>();
        services.AddScoped<IFinanceReportService, FinanceReportService>();

        _provider = services.BuildServiceProvider();

        CreateTables();
    }

    /// <summary>Runs <paramref name="work"/> in its own DI scope, as a delivery would.</summary>
    public async Task<TResult> InScopeAsync<TResult>(Func<IServiceProvider, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = _provider.CreateAsyncScope();

        return await work(scope.ServiceProvider);
    }

    /// <summary>Runs <paramref name="work"/> in its own scope, for work with nothing to return.</summary>
    public Task InScopeAsync(Func<IServiceProvider, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync<object?>(async services =>
        {
            await work(services);

            return null;
        });
    }

    /// <summary>Posts <paramref name="posting"/> through the real ledger, in its own scope.</summary>
    public Task PostAsync(JournalPostingIntent posting) =>
        InScopeAsync(services => services.GetRequiredService<IJournalPostingSeam>().PostAsync(posting));

    /// <summary>
    /// Delivers <paramref name="event"/> to <paramref name="consume"/> through the real idempotent
    /// handler, exactly as <c>IdempotentConsumer</c> does — claim, unit of work and all.
    /// </summary>
    /// <returns><see langword="true"/> if the handler ran; <see langword="false"/> for a redelivery.</returns>
    public Task<bool> DeliverAsync(
        Guid eventId,
        string consumerName,
        Func<IJournalPostingSeam, CancellationToken, Task> consume)
    {
        ArgumentNullException.ThrowIfNull(consume);

        return InScopeAsync(services => services.GetRequiredService<IdempotentEventHandler>().HandleAsync(
            eventId,
            consumerName,
            token => consume(services.GetRequiredService<IJournalPostingSeam>(), token)));
    }

    /// <summary>Runs <paramref name="work"/> against the ledger's reports, in its own scope.</summary>
    public Task<TResult> WithReportsAsync<TResult>(Func<IFinanceReportService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IFinanceReportService>()));
    }

    /// <summary>Runs <paramref name="work"/> against the ledger listing, in its own scope.</summary>
    public Task<TResult> WithJournalAsync<TResult>(Func<IJournalService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IJournalService>()));
    }

    /// <summary>Reads back what a posting wrote, on a context outside any unit of work.</summary>
    public FinanceDbContext NewFinanceContext() =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseSqlite(_connection).Options);

    /// <summary>Reads back the audit trail a posting produced.</summary>
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
    /// emits the configuration's <c>HasData</c> inserts, which is how the shipped chart of accounts
    /// reaches the fast tier without a migration — and the ledger cannot post without it.
    /// </summary>
    private void CreateTables()
    {
        using var scope = _provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>().CreateTables();

        scope.ServiceProvider.GetRequiredService<FinanceDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>().CreateTables();
    }
}
