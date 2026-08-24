using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Assets.Features.Assets;

/// <summary>Maps <see cref="Asset"/> onto <c>assets.assets</c>.</summary>
public sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("assets");

        builder.HasKey(asset => asset.Id).HasName("pk_assets");

        // Never store-generated: every id in GridCore is a Guid v7 minted in code from the clock,
        // and saying so is load-bearing rather than cosmetic. EF decides whether an untracked child
        // of a tracked parent is an insert or an update by asking whether its key is set on a
        // store-generated column — leave the default and a freshly appended history line is tracked
        // as Modified, and the save fails having updated nothing (WP-1.2 lost half an hour to this).
        builder.Property(asset => asset.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(asset => asset.AssetTag)
            .HasColumnName("asset_tag")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        // Stored by name: a record read years from now must not depend on today's enum ordering.
        builder.Property(asset => asset.Class)
            .HasColumnName("class")
            .HasConversion<string>()
            .HasMaxLength(Asset.EnumNameLength)
            .IsRequired();

        builder.Property(asset => asset.Name).HasColumnName("name").HasMaxLength(Asset.NameLength).IsRequired();
        builder.Property(asset => asset.SerialNumber).HasColumnName("serial_number").HasMaxLength(Asset.SerialNumberLength);
        builder.Property(asset => asset.Manufacturer).HasColumnName("manufacturer").HasMaxLength(Asset.ModelLength);
        builder.Property(asset => asset.Model).HasColumnName("model").HasMaxLength(Asset.ModelLength);
        builder.Property(asset => asset.InstalledOn).HasColumnName("installed_on");

        builder.Property(asset => asset.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(Asset.EnumNameLength)
            .IsRequired();

        builder.Property(asset => asset.Condition)
            .HasColumnName("condition")
            .HasConversion<string>()
            .HasMaxLength(Asset.EnumNameLength)
            .IsRequired();

        // Two plain nullable columns, and Asset.Position is computed from them. Not an owned type:
        // EF does not map an owned struct at all, and an owned class whose properties are both
        // required cannot be an optional dependent in a shared table — it could not tell "no
        // position recorded" from "a position of zero, zero". Both-or-neither is enforced by the
        // aggregate instead, which is the only thing that can set them.
        builder.Property(asset => asset.Latitude)
            .HasColumnName("latitude")
            .HasPrecision(GeoPosition.Precision, GeoPosition.DecimalPlaces);

        builder.Property(asset => asset.Longitude)
            .HasColumnName("longitude")
            .HasPrecision(GeoPosition.Precision, GeoPosition.DecimalPlaces);

        builder.Ignore(asset => asset.Position);

        builder.Property(asset => asset.LocationNote).HasColumnName("location_note").HasMaxLength(Asset.LocationNoteLength);
        builder.Property(asset => asset.RegisteredAt).HasColumnName("registered_at");
        builder.Property(asset => asset.StatusChangedAt).HasColumnName("status_changed_at");
        builder.Property(asset => asset.StatusReason).HasColumnName("status_reason").HasMaxLength(Asset.ReasonLength);
        builder.Property(asset => asset.ConditionAssessedAt).HasColumnName("condition_assessed_at");

        // Append-only, owned by the asset and loaded through it. Backing field rather than the
        // property: History is IReadOnlyList so nothing outside the aggregate can add a line.
        builder.HasMany(asset => asset.History)
            .WithOne()
            .HasForeignKey(entry => entry.AssetId)
            .HasConstraintName("fk_asset_history_asset")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Asset.History))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(asset => asset.AssetTag).HasDatabaseName("ux_assets_asset_tag").IsUnique();

        // One physical machine, one record. Unfiltered rather than filtered on "not null": both
        // Postgres and the fast tier's SQLite treat NULLs in a unique index as distinct, so the
        // pole and the span of conductor that carry no serial are unaffected — and this needs no
        // hand-written SQL predicate to keep in step with the column name (WP-1.2's lesson).
        builder.HasIndex(asset => asset.SerialNumber).HasDatabaseName("ux_assets_serial_number").IsUnique();

        // The registry screens filter on these three; a maintenance plan is a query over the last
        // two together ("everything in service and Poor or worse").
        builder.HasIndex(asset => asset.Class).HasDatabaseName("ix_assets_class");
        builder.HasIndex(asset => asset.Status).HasDatabaseName("ix_assets_status");
        builder.HasIndex(asset => asset.Condition).HasDatabaseName("ix_assets_condition");
    }
}

/// <summary>Maps <see cref="AssetHistoryEntry"/> onto <c>assets.asset_history</c>.</summary>
public sealed class AssetHistoryEntryConfiguration : IEntityTypeConfiguration<AssetHistoryEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AssetHistoryEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("asset_history");

        builder.HasKey(entry => entry.Id).HasName("pk_asset_history");

        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(entry => entry.AssetId).HasColumnName("asset_id");

        builder.Property(entry => entry.EntryType)
            .HasColumnName("entry_type")
            .HasConversion<string>()
            .HasMaxLength(Asset.EnumNameLength)
            .IsRequired();

        builder.Property(entry => entry.FromStatus)
            .HasColumnName("from_status")
            .HasConversion<string>()
            .HasMaxLength(Asset.EnumNameLength);

        builder.Property(entry => entry.ToStatus)
            .HasColumnName("to_status")
            .HasConversion<string>()
            .HasMaxLength(Asset.EnumNameLength);

        builder.Property(entry => entry.FromCondition)
            .HasColumnName("from_condition")
            .HasConversion<string>()
            .HasMaxLength(Asset.EnumNameLength);

        builder.Property(entry => entry.ToCondition)
            .HasColumnName("to_condition")
            .HasConversion<string>()
            .HasMaxLength(Asset.EnumNameLength);

        builder.Property(entry => entry.Note).HasColumnName("note").HasMaxLength(AssetHistoryEntry.NoteLength);

        // No foreign key: Work Orders is another module and another schema, and this module must
        // never reach into its tables. WP-3.4 stamps the id; a screen showing the job resolves it
        // through that module's service, not through a join.
        builder.Property(entry => entry.WorkOrderId).HasColumnName("work_order_id");

        builder.Property(entry => entry.ActorId).HasColumnName("actor_id").HasMaxLength(RegistryActor.MaxLength).IsRequired();
        builder.Property(entry => entry.ActorName).HasColumnName("actor_name").HasMaxLength(RegistryActor.MaxLength);
        builder.Property(entry => entry.RecordedAt).HasColumnName("recorded_at");

        builder.HasIndex(entry => entry.AssetId).HasDatabaseName("ix_asset_history_asset_id");

        // WP-3.4 reads the maintenance lines on their own — "what work has this asset had" is the
        // read model's whole purpose, and it is a filter on a table that grows with every
        // assessment.
        builder.HasIndex(entry => entry.EntryType).HasDatabaseName("ix_asset_history_entry_type");
    }
}
