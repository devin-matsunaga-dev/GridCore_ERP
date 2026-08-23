using GridCore.Platform.Approvals;
using GridCore.Platform.Audit;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Platform.Data;

/// <summary>
/// The platform's own schema: the audit trail and the approval queue. Modules own their schemas the
/// same way and never read this one directly — <see cref="IAuditLog"/> and
/// <see cref="IApprovalService"/> are the seams.
/// </summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    /// <summary>The Postgres schema this context owns.</summary>
    public const string SchemaName = "platform";

    /// <summary>Table that records applied migrations, kept inside the platform schema.</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>Column type used for JSON snapshots on Postgres; rewritten for other providers.</summary>
    public const string JsonColumnType = "jsonb";

    /// <summary>The append-only audit trail.</summary>
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    /// <summary>Approval requests, pending and decided.</summary>
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();

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
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlatformDbContext).Assembly);

        // The relational model targets Postgres. The fast test tier runs the same model on SQLite,
        // which has no jsonb, so the column type is relaxed rather than duplicating the model.
        if (!Database.IsNpgsql())
        {
            foreach (var property in modelBuilder.Model
                .GetEntityTypes()
                .SelectMany(entity => entity.GetProperties())
                .Where(property => property.GetColumnType() == JsonColumnType))
            {
                property.SetColumnType(null);
            }
        }
    }

    /// <summary>
    /// Invariant 1 of ARCHITECTURE.md in code: the audit trail only ever grows. A correction is a
    /// new entry, exactly as with the ledger — so an attempt to rewrite history fails loudly rather
    /// than succeeding quietly.
    /// </summary>
    /// <exception cref="InvalidOperationException">An <see cref="AuditEntry"/> is being modified or deleted.</exception>
    private void GuardAppendOnly()
    {
        ChangeTracker.DetectChanges();

        var tampered = ChangeTracker
            .Entries<AuditEntry>()
            .FirstOrDefault(entry => entry.State is EntityState.Modified or EntityState.Deleted);

        if (tampered is not null)
        {
            throw new InvalidOperationException(
                $"The audit trail is append-only; entry '{tampered.Entity.Id}' cannot be {tampered.State.ToString().ToLowerInvariant()}.");
        }
    }
}
