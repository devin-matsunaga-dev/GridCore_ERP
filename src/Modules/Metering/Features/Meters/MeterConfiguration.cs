using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Metering.Features.Meters;

/// <summary>Maps <see cref="Meter"/> onto <c>metering.meters</c>.</summary>
public sealed class MeterConfiguration : IEntityTypeConfiguration<Meter>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Meter> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("meters");

        builder.HasKey(meter => meter.Id).HasName("pk_meters");

        // Never store-generated: every id in GridCore is a Guid v7 minted in code from the clock,
        // and saying so is load-bearing rather than cosmetic. EF decides whether an untracked child
        // of a tracked parent is an insert or an update by asking whether its key is set on a
        // store-generated column — leave the default and a freshly appended history line is tracked
        // as Modified, and the save fails having updated nothing (WP-1.2 lost half an hour to this).
        builder.Property(meter => meter.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(meter => meter.MeterNumber)
            .HasColumnName("meter_number")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        builder.Property(meter => meter.SerialNumber)
            .HasColumnName("serial_number")
            .HasMaxLength(Meter.SerialNumberLength)
            .IsRequired();

        // Stored by name: a record read years from now must not depend on today's enum ordering.
        builder.Property(meter => meter.Type)
            .HasColumnName("type")
            .HasConversion<string>()
            .HasMaxLength(Meter.EnumNameLength)
            .IsRequired();

        builder.Property(meter => meter.Manufacturer).HasColumnName("manufacturer").HasMaxLength(Meter.ModelLength);
        builder.Property(meter => meter.Model).HasColumnName("model").HasMaxLength(Meter.ModelLength);

        builder.Property(meter => meter.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(Meter.EnumNameLength)
            .IsRequired();

        // No foreign key: Customers is another module and another schema, and this module must
        // never reach into its tables. The premise is checked through IServiceLocationDirectory
        // before it is ever set (ARCHITECTURE.md's boundary rule) — the same shape WP-1.3 used for
        // an asset history line's work order id.
        builder.Property(meter => meter.ServiceLocationId).HasColumnName("service_location_id");

        builder.Property(meter => meter.InstalledAt).HasColumnName("installed_at");

        builder.Property(meter => meter.InstallationReading)
            .HasColumnName("installation_reading")
            .HasPrecision(Meter.DialPrecision, Meter.DialDecimalPlaces);

        builder.Property(meter => meter.RegisteredAt).HasColumnName("registered_at");
        builder.Property(meter => meter.StatusChangedAt).HasColumnName("status_changed_at");
        builder.Property(meter => meter.StatusReason).HasColumnName("status_reason").HasMaxLength(Meter.ReasonLength);

        // Append-only, owned by the meter and loaded through it. Backing field rather than the
        // property: History is IReadOnlyList so nothing outside the aggregate can add a line.
        builder.HasMany(meter => meter.History)
            .WithOne()
            .HasForeignKey(entry => entry.MeterId)
            .HasConstraintName("fk_meter_history_meter")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Meter.History))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(meter => meter.IsFitted);
        builder.Ignore(meter => meter.AllowedTransitions);
        builder.Ignore(meter => meter.AllowedStatusChanges);

        builder.HasIndex(meter => meter.MeterNumber).HasDatabaseName("ux_meters_meter_number").IsUnique();

        // One physical device, one record. Unfiltered rather than filtered on "not null": the
        // column is required, so there are no NULLs to reason about, and this needs no hand-written
        // SQL predicate to keep in step with the column name (WP-1.2's lesson).
        builder.HasIndex(meter => meter.SerialNumber).HasDatabaseName("ux_meters_serial_number").IsUnique();

        // ONE ACTIVE METER PER SERVICE LOCATION, as a database fact rather than a convention.
        //
        // No filter, and that is the point. A meter holds a premise exactly while it is fitted
        // (Meter keeps ServiceLocationId and Status in step), and a removal sets the column back to
        // NULL — and both Postgres and the fast tier's SQLite treat NULLs in a unique index as
        // distinct, so any number of meters can sit in a store together. So "at most one meter is
        // fitted at a premise" falls straight out of the column, with no SQL predicate naming a
        // status that a later rename could quietly desynchronise (the failure WP-1.2's two guard
        // tests exist to catch).
        //
        // Deliberately independent of `ux_service_accounts_open_location`, which is Customers' rule
        // that one premise has one open account (owner's call). The two constrain different things:
        // a premise can be metered before anyone is billed there, and an account stays open across
        // a meter exchange. Neither index knows about the other, and neither should.
        builder.HasIndex(meter => meter.ServiceLocationId).HasDatabaseName("ux_meters_service_location").IsUnique();

        // The register filters on status, and "what is in stock" is the question a store asks.
        builder.HasIndex(meter => meter.Status).HasDatabaseName("ix_meters_status");
        builder.HasIndex(meter => meter.Type).HasDatabaseName("ix_meters_type");
    }
}

/// <summary>Maps <see cref="MeterHistoryEntry"/> onto <c>metering.meter_history</c>.</summary>
public sealed class MeterHistoryEntryConfiguration : IEntityTypeConfiguration<MeterHistoryEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MeterHistoryEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("meter_history");

        builder.HasKey(entry => entry.Id).HasName("pk_meter_history");

        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(entry => entry.MeterId).HasColumnName("meter_id");

        builder.Property(entry => entry.EntryType)
            .HasColumnName("entry_type")
            .HasConversion<string>()
            .HasMaxLength(Meter.EnumNameLength)
            .IsRequired();

        builder.Property(entry => entry.FromStatus)
            .HasColumnName("from_status")
            .HasConversion<string>()
            .HasMaxLength(Meter.EnumNameLength);

        builder.Property(entry => entry.ToStatus)
            .HasColumnName("to_status")
            .HasConversion<string>()
            .HasMaxLength(Meter.EnumNameLength)
            .IsRequired();

        // No foreign key, for the same reason as the meter's own column.
        builder.Property(entry => entry.ServiceLocationId).HasColumnName("service_location_id");

        builder.Property(entry => entry.Note).HasColumnName("note").HasMaxLength(MeterHistoryEntry.NoteLength);
        builder.Property(entry => entry.ActorId).HasColumnName("actor_id").HasMaxLength(RegistryActor.MaxLength).IsRequired();
        builder.Property(entry => entry.ActorName).HasColumnName("actor_name").HasMaxLength(RegistryActor.MaxLength);
        builder.Property(entry => entry.RecordedAt).HasColumnName("recorded_at");

        builder.HasIndex(entry => entry.MeterId).HasDatabaseName("ix_meter_history_meter_id");

        // "Which meter was measuring this premise in March" is a query on this column, and it is the
        // whole reason the premise is stamped on the line rather than read off the meter — which by
        // then is on somebody else's wall.
        builder.HasIndex(entry => entry.ServiceLocationId).HasDatabaseName("ix_meter_history_service_location_id");
    }
}
