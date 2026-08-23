using GridCore.Platform.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Platform.UnitTests.Data;

/// <summary>
/// The real <see cref="PlatformDbContext"/> model on a private SQLite in-memory database — per
/// CONVENTIONS.md rule C, a calculation or a mapping is never worth a Postgres container. Each
/// instance owns its own connection, so tests stay parallel-safe.
/// </summary>
public sealed class PlatformTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public PlatformTestDatabase()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        Context = NewContext();
        Context.Database.EnsureCreated();
    }

    /// <summary>The context under test.</summary>
    public PlatformDbContext Context { get; }

    /// <summary>A second context over the same database, for reading back what a test wrote.</summary>
    public PlatformDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
