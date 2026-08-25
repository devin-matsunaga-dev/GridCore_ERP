using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.Journal;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Finance.Data;

/// <summary>
/// The Finance module's schema: the chart of accounts and the general ledger that posts to it.
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

    /// <summary>The general ledger: one row per balanced journal entry.</summary>
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();

    /// <summary>The debits and credits those entries are made of.</summary>
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAppendOnly();

        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardAppendOnly();

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FinanceDbContext).Assembly);
    }

    /// <summary>
    /// Invariant 3 of ARCHITECTURE.md in code: the ledger only ever grows. A correction is a new
    /// entry — which is exactly what a <c>BillAdjusted</c> posting is — so an attempt to rewrite
    /// history fails loudly rather than succeeding quietly.
    /// </summary>
    /// <remarks>
    /// The same guard <c>PlatformDbContext</c> puts on the audit trail, and deliberately worded the
    /// same way. It covers the lines as well as the entries: an entry whose lines could be edited
    /// balances exactly as long as nobody has edited them. The chart of accounts is <i>not</i>
    /// covered — it is reference data, changed only by migration, and a migration is how an account
    /// is meant to be corrected.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A ledger row is being modified or deleted.</exception>
    private void GuardAppendOnly()
    {
        ChangeTracker.DetectChanges();

        var tamperedEntry = ChangeTracker
            .Entries<JournalEntry>()
            .FirstOrDefault(entry => entry.State is EntityState.Modified or EntityState.Deleted);

        if (tamperedEntry is not null)
        {
            throw new InvalidOperationException(
                $"The general ledger is append-only; entry '{tamperedEntry.Entity.EntryNumber}' cannot be "
                + $"{tamperedEntry.State.ToString().ToLowerInvariant()}. A correction is a new entry.");
        }

        var tamperedLine = ChangeTracker
            .Entries<JournalLine>()
            .FirstOrDefault(line => line.State is EntityState.Modified or EntityState.Deleted);

        if (tamperedLine is not null)
        {
            throw new InvalidOperationException(
                $"The general ledger is append-only; line '{tamperedLine.Entity.Id}' cannot be "
                + $"{tamperedLine.State.ToString().ToLowerInvariant()}. A correction is a new entry.");
        }
    }
}
