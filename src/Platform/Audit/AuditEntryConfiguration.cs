using GridCore.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Platform.Audit;

/// <summary>Maps <see cref="AuditEntry"/> onto <c>platform.audit_entries</c>.</summary>
public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_entries");

        builder.HasKey(entry => entry.Id).HasName("pk_audit_entries");

        builder.Property(entry => entry.Id).HasColumnName("id");
        builder.Property(entry => entry.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(entry => entry.UserId).HasColumnName("user_id").HasMaxLength(AuditEntry.NameLength).IsRequired();
        builder.Property(entry => entry.UserName).HasColumnName("user_name").HasMaxLength(AuditEntry.NameLength);
        builder.Property(entry => entry.Action).HasColumnName("action").HasMaxLength(AuditEntry.NameLength).IsRequired();
        builder.Property(entry => entry.EntityType).HasColumnName("entity_type").HasMaxLength(AuditEntry.NameLength).IsRequired();
        builder.Property(entry => entry.EntityId).HasColumnName("entity_id").HasMaxLength(AuditEntry.NameLength).IsRequired();
        builder.Property(entry => entry.BeforeJson).HasColumnName("before_json").HasColumnType(PlatformDbContext.JsonColumnType);
        builder.Property(entry => entry.AfterJson).HasColumnName("after_json").HasColumnType(PlatformDbContext.JsonColumnType);
        builder.Property(entry => entry.CorrelationId).HasColumnName("correlation_id").HasMaxLength(AuditEntry.NameLength);

        // The trail is read two ways: "what happened to this entity" and "what did this user do".
        builder.HasIndex(entry => new { entry.EntityType, entry.EntityId }).HasDatabaseName("ix_audit_entries_entity");
        builder.HasIndex(entry => entry.UserId).HasDatabaseName("ix_audit_entries_user_id");
        builder.HasIndex(entry => entry.OccurredAt).HasDatabaseName("ix_audit_entries_occurred_at");
    }
}
