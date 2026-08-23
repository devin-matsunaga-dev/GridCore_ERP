using GridCore.Platform.Approvals;
using GridCore.Platform.Audit;
using GridCore.Platform.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Platform.Data;

/// <summary>
/// The platform's own schema: the audit trail, the approval queue, the transactional outbox and the
/// consumer dedupe table. Modules own their schemas the same way and never read this one directly —
/// <see cref="IAuditLog"/>, <see cref="IApprovalService"/>, <see cref="IEventPublisher"/> and
/// <see cref="IMessageDeduplicator"/> are the seams.
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

    /// <summary>Events each consumer has already handled — the dedupe helper's memory.</summary>
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

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

        // MassTransit's outbox and inbox tables live in the platform schema alongside everything
        // else the platform owns, which is what lets a publish share a transaction with the write
        // that caused it. Only the table names are restated in snake_case per CONVENTIONS.md; the
        // columns keep the library's names because the library queries its own model, and renaming
        // them buys nothing but a migration to get wrong.
        modelBuilder.AddInboxStateEntity(entity => entity.ToTable("inbox_state"));
        modelBuilder.AddOutboxMessageEntity(entity => entity.ToTable("outbox_message"));
        modelBuilder.AddOutboxStateEntity(entity => entity.ToTable("outbox_state"));

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
