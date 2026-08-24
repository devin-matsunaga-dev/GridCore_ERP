using GridCore.Modules.Inventory.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Inventory.UnitTests.Warehouses;

/// <summary>
/// The real <see cref="InventoryDbContext"/> model on a private SQLite in-memory database — per
/// CONVENTIONS.md rule C. <c>EnsureCreated</c> applies the configuration's seed data, so this is
/// also how the fast tier proves the warehouses arrive with the schema.
/// </summary>
public sealed class InventoryTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public InventoryTestDatabase()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        Context = NewContext();
        Context.Database.EnsureCreated();
    }

    /// <summary>The context under test.</summary>
    public InventoryDbContext Context { get; }

    /// <summary>A second context over the same database, for reading back what was seeded.</summary>
    public InventoryDbContext NewContext() =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
