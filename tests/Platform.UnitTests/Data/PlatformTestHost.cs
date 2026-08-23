using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GridCore.Platform.UnitTests.Data;

/// <summary>
/// Stands in for a module's own <see cref="DbContext"/>. Modules land from WP-1.1, but the whole
/// point of <see cref="IUnitOfWork"/> is atomicity <i>across</i> contexts, so the fast tier needs a
/// second one to prove it against.
/// </summary>
public sealed class ModuleTestDbContext(DbContextOptions<ModuleTestDbContext> options) : DbContext(options)
{
    public DbSet<ModuleRow> Rows => Set<ModuleRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ModuleRow>(ConfigureRow);

    private static void ConfigureRow(EntityTypeBuilder<ModuleRow> builder)
    {
        builder.ToTable("module_rows");
        builder.HasKey(row => row.Id).HasName("pk_module_rows");
        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.Name).HasColumnName("name").IsRequired();
    }
}

/// <summary>A row in the pretend module's schema.</summary>
public sealed class ModuleRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// A host with two contexts on one SQLite in-memory connection — the fast-tier equivalent of a
/// module schema and the platform schema sharing a Postgres connection. Per CONVENTIONS.md rule C,
/// proving that a transaction spans two contexts, or that a consumer runs exactly once, does not
/// need a container: it needs a database, and SQLite in-memory is one.
/// </summary>
public sealed class PlatformTestHost : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public PlatformTestHost(TimeProvider? clock = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var services = new ServiceCollection();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(clock ?? TimeProvider.System);

        // ownsConnection: false — the in-memory database lives only as long as this connection,
        // and each scope disposing it would delete the database mid-test.
        services.AddGridCoreDataAccess(_ => new GridCoreDbConnection(_connection, ownsConnection: false));
        services.AddGridCoreDbContext<PlatformDbContext>((builder, connection) => builder.UseSqlite(connection));
        services.AddGridCoreDbContext<ModuleTestDbContext>((builder, connection) => builder.UseSqlite(connection));

        // The consume path minus the bus: the deduplicator and the idempotent handler are plain
        // services, which is exactly why the whole path is testable in the fast tier.
        services.AddScoped<IMessageDeduplicator, MessageDeduplicator>();
        services.AddScoped<IdempotentEventHandler>();

        _provider = services.BuildServiceProvider();

        CreateTables();
    }

    /// <summary>Runs <paramref name="work"/> in its own DI scope, as a request or a message would.</summary>
    public async Task<TResult> InScopeAsync<TResult>(Func<IServiceProvider, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = _provider.CreateAsyncScope();

        return await work(scope.ServiceProvider);
    }

    /// <summary>Reads back what a test wrote, on a context outside any unit of work.</summary>
    public PlatformDbContext NewPlatformContext() =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(_connection).Options);

    /// <summary>Reads back what a test wrote to the pretend module's schema.</summary>
    public ModuleTestDbContext NewModuleContext() =>
        new(new DbContextOptionsBuilder<ModuleTestDbContext>().UseSqlite(_connection).Options);

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

        scope.ServiceProvider.GetRequiredService<ModuleTestDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>().CreateTables();
    }
}
