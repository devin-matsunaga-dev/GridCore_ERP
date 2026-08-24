using GridCore.Modules.Finance.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Finance.UnitTests.ChartOfAccounts;

/// <summary>
/// The real <see cref="FinanceDbContext"/> model on a private SQLite in-memory database — per
/// CONVENTIONS.md rule C, a mapping is never worth a Postgres container. <c>EnsureCreated</c>
/// applies the configuration's seed data, so this is also how the fast tier proves that reference
/// data actually ships with the schema.
/// </summary>
public sealed class FinanceTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public FinanceTestDatabase()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        Context = NewContext();
        Context.Database.EnsureCreated();
    }

    /// <summary>The context under test.</summary>
    public FinanceDbContext Context { get; }

    /// <summary>A second context over the same database, for reading back what was seeded.</summary>
    public FinanceDbContext NewContext() =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
