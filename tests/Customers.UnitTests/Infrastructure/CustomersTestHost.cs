using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
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

namespace GridCore.Modules.Customers.UnitTests.Infrastructure;

/// <summary>
/// The customers schema and the platform schema on one SQLite in-memory connection — the fast-tier
/// equivalent of the shared Postgres connection the host gives a request. That is what lets these
/// tests assert the thing that actually matters about a registry write: the customer row, its audit
/// entry and its event all belong to one transaction (CONVENTIONS.md rule C).
/// </summary>
public sealed class CustomersTestHost : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    /// <param name="clock">The clock the host uses; a <see cref="FakeClock"/> keeps tests off wall time.</param>
    /// <param name="currentUser">Who is acting. Defaults to the system, as background work is.</param>
    public CustomersTestHost(TimeProvider? clock = null, ICurrentUser? currentUser = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var services = new ServiceCollection();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddSingleton(currentUser ?? SystemUser.Instance);
        services.AddSingleton<IEventPublisher>(Events);

        // ownsConnection: false — the in-memory database lives only as long as this connection, and
        // each scope disposing it would delete the database mid-test.
        services.AddGridCoreDataAccess(_ => new GridCoreDbConnection(_connection, ownsConnection: false));
        services.AddGridCoreDbContext<PlatformDbContext>((builder, connection) => builder.UseSqlite(connection));
        services.AddGridCoreDbContext<CustomersDbContext>((builder, connection) => builder.UseSqlite(connection));

        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IRegistryNumberGenerator, SequentialRegistryNumberGenerator>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IServiceLocationService, ServiceLocationService>();
        services.AddScoped<IServiceAccountService, ServiceAccountService>();
        services.AddScoped<IServiceLocationDirectory, ServiceLocationDirectory>();
        services.AddScoped<IServiceAccountDirectory, ServiceAccountDirectory>();

        _provider = services.BuildServiceProvider();

        CreateTables();
    }

    /// <summary>Everything the registry published while the test ran.</summary>
    public RecordingEventPublisher Events { get; } = new();

    /// <summary>Runs <paramref name="work"/> in its own DI scope, as a request would.</summary>
    public async Task<TResult> InScopeAsync<TResult>(Func<IServiceProvider, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = _provider.CreateAsyncScope();

        return await work(scope.ServiceProvider);
    }

    /// <summary>Runs <paramref name="work"/> against the customer registry, in its own scope.</summary>
    public Task<TResult> WithCustomersAsync<TResult>(Func<ICustomerService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<ICustomerService>()));
    }

    /// <summary>Runs <paramref name="work"/> against the location registry, in its own scope.</summary>
    public Task<TResult> WithLocationsAsync<TResult>(Func<IServiceLocationService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IServiceLocationService>()));
    }

    /// <summary>Runs <paramref name="work"/> against the service account registry, in its own scope.</summary>
    public Task<TResult> WithAccountsAsync<TResult>(Func<IServiceAccountService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IServiceAccountService>()));
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the premise registry <i>as another module sees it</i> —
    /// the cross-module read seam, resolved from the container exactly as Metering resolves it.
    /// </summary>
    public Task<TResult> WithDirectoryAsync<TResult>(Func<IServiceLocationDirectory, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IServiceLocationDirectory>()));
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the service account registry <i>as another module sees
    /// it</i> — the seam Billing (WP-2.3) raises every bill through, resolved from the container
    /// exactly as Billing resolves it.
    /// </summary>
    public Task<TResult> WithAccountDirectoryAsync<TResult>(Func<IServiceAccountDirectory, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IServiceAccountDirectory>()));
    }

    /// <summary>Reads back what a test wrote, on a context outside any unit of work.</summary>
    public CustomersDbContext NewCustomersContext() =>
        new(new DbContextOptionsBuilder<CustomersDbContext>().UseSqlite(_connection).Options);

    /// <summary>Reads back the audit trail a registry write produced.</summary>
    public PlatformDbContext NewPlatformContext() =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Creates both schemas. <c>EnsureCreated</c> cannot do this: it returns false once the
    /// database exists, so the second context's tables would silently never be created.
    /// </summary>
    private void CreateTables()
    {
        using var scope = _provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>().CreateTables();

        scope.ServiceProvider.GetRequiredService<CustomersDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>().CreateTables();
    }
}
