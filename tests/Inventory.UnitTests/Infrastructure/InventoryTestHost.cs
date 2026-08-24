using GridCore.Modules.Inventory.Data;
using GridCore.Modules.Inventory.Features.Items;
using GridCore.Modules.Inventory.Features.Shared;
using GridCore.Modules.Inventory.Features.Warehouses;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GridCore.Modules.Inventory.UnitTests.Infrastructure;

/// <summary>
/// The inventory schema and the platform schema on one SQLite in-memory connection — the fast-tier
/// equivalent of the shared Postgres connection the host gives a request. That is what lets these
/// tests assert the thing that actually matters about a stock movement: the level, its ledger line
/// and its audit entry all belong to one transaction (CONVENTIONS.md rule C).
/// </summary>
public sealed class InventoryTestHost : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    /// <param name="clock">The clock the host uses; a <see cref="FakeClock"/> keeps tests off wall time.</param>
    /// <param name="currentUser">Who is acting. Defaults to the system, as background work is.</param>
    public InventoryTestHost(TimeProvider? clock = null, ICurrentUser? currentUser = null)
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
        services.AddGridCoreDbContext<InventoryDbContext>((builder, connection) => builder.UseSqlite(connection));

        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IStockItemNumberGenerator, SequentialStockItemNumberGenerator>();
        services.AddScoped<IStockItemService, StockItemService>();
        services.AddScoped<IWarehouseService, WarehouseService>();

        _provider = services.BuildServiceProvider();

        CreateTables();
    }

    /// <summary>The main store on Saipan, resolved the way production code does — from the shipped reference set.</summary>
    public static Guid LowerBase { get; } = DefaultWarehouses.Require(DefaultWarehouses.LowerBase).Id;

    /// <summary>The Rota store.</summary>
    public static Guid Rota { get; } = DefaultWarehouses.Require(DefaultWarehouses.Rota).Id;

    /// <summary>The Tinian store.</summary>
    public static Guid Tinian { get; } = DefaultWarehouses.Require(DefaultWarehouses.Tinian).Id;

    /// <summary>Runs <paramref name="work"/> in its own DI scope, as a request would.</summary>
    public async Task<TResult> InScopeAsync<TResult>(Func<IServiceProvider, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = _provider.CreateAsyncScope();

        return await work(scope.ServiceProvider);
    }

    /// <summary>Runs <paramref name="work"/> against the store, in its own scope.</summary>
    public Task<TResult> WithStockAsync<TResult>(Func<IStockItemService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IStockItemService>()));
    }

    /// <summary>Registers an item and returns it, for a test whose subject is what happens next.</summary>
    public Task<StockItem> GivenItemAsync(
        string name = "ACSR Raven 1/0 conductor",
        UnitOfMeasure unit = UnitOfMeasure.Metre,
        StockItemCategory category = StockItemCategory.Conductor,
        decimal unitCost = 4.85m,
        string? partNumber = null) =>
        WithStockAsync(stock => stock.RegisterAsync(
            new RegisterStockItemInput(category, name, unit, ManufacturerPartNumber: partNumber, UnitCost: unitCost)));

    /// <summary>Reads back what a test wrote, on a context outside any unit of work.</summary>
    public InventoryDbContext NewInventoryContext() =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseSqlite(_connection).Options);

    /// <summary>Reads back the audit trail a stock write produced.</summary>
    public PlatformDbContext NewPlatformContext() =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Creates both schemas. <c>EnsureCreated</c> cannot do this: it returns false once the database
    /// exists, so the second context's tables would silently never be created.
    /// </summary>
    /// <remarks>
    /// <c>CreateTables</c> emits the configuration's <c>HasData</c> inserts along with the DDL, so
    /// the three shipped warehouses land here exactly as the migration lands them in production —
    /// which matters, because a stock level has a real foreign key to one and SQLite enforces it.
    /// </remarks>
    private void CreateTables()
    {
        using var scope = _provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>().CreateTables();

        scope.ServiceProvider.GetRequiredService<InventoryDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>().CreateTables();
    }
}
