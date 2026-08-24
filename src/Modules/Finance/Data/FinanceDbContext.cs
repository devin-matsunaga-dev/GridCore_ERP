using GridCore.Modules.Finance.Features.ChartOfAccounts;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Finance.Data;

/// <summary>
/// The Finance module's schema. Today it holds the chart of accounts; WP-2.6 adds the journal
/// entries that post to it.
/// </summary>
/// <remarks>
/// Finance is downstream of everyone (ARCHITECTURE.md): nothing outside this module reads these
/// tables, and Finance reads no other module's. It learns what happened from events.
/// </remarks>
public sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options) : DbContext(options)
{
    /// <summary>The Postgres schema this context owns — also the module's name.</summary>
    public const string SchemaName = "finance";

    /// <summary>The chart of accounts.</summary>
    public DbSet<Account> Accounts => Set<Account>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FinanceDbContext).Assembly);
    }
}
