using GridCore.Modules.Billing.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.UnitTests.RatePlans;

/// <summary>
/// The real <see cref="BillingDbContext"/> model on a private SQLite in-memory database — per
/// CONVENTIONS.md rule C. <c>EnsureCreated</c> applies the configuration's seed data, so this is
/// also how the fast tier proves the shipped tariffs arrive with the schema.
/// </summary>
public sealed class BillingTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public BillingTestDatabase()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        Context = NewContext();
        Context.Database.EnsureCreated();
    }

    /// <summary>The context under test.</summary>
    public BillingDbContext Context { get; }

    /// <summary>A second context over the same database, for reading back what was seeded.</summary>
    public BillingDbContext NewContext() =>
        new(new DbContextOptionsBuilder<BillingDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
