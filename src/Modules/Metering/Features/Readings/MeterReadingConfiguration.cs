using GridCore.Modules.Metering.Features.Meters;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Metering.Features.Readings;

/// <summary>Maps <see cref="MeterReading"/> onto <c>metering.meter_readings</c>.</summary>
public sealed class MeterReadingConfiguration : IEntityTypeConfiguration<MeterReading>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MeterReading> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("meter_readings");

        builder.HasKey(reading => reading.Id).HasName("pk_meter_readings");

        // Never store-generated — WP-1.2's lesson, and the reason every id in GridCore is a Guid v7
        // minted from the clock in code.
        builder.Property(reading => reading.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(reading => reading.MeterId).HasColumnName("meter_id");

        // No foreign key: Customers is another module over another schema. The premise is the one
        // the meter was checked onto through IServiceLocationDirectory when it was fitted.
        builder.Property(reading => reading.ServiceLocationId).HasColumnName("service_location_id");

        builder.Property(reading => reading.ReadingDate).HasColumnName("reading_date");

        // decimal, never double — dials are money's neighbour and the same rule applies. Nullable
        // exactly and only for a missing read.
        builder.Property(reading => reading.Reading)
            .HasColumnName("reading")
            .HasPrecision(MeterReading.Precision, MeterReading.DecimalPlaces);

        // Stored by name: a reading read back years from now must not depend on today's enum ordering.
        builder.Property(reading => reading.Source)
            .HasColumnName("source")
            .HasConversion<string>()
            .HasMaxLength(Meter.EnumNameLength)
            .IsRequired();

        builder.Property(reading => reading.PreviousReading)
            .HasColumnName("previous_reading")
            .HasPrecision(MeterReading.Precision, MeterReading.DecimalPlaces);

        builder.Property(reading => reading.PreviousReadingDate).HasColumnName("previous_reading_date");

        // Stamped, not derived on read. The line has to still say what it said after the meter's
        // register width is corrected or the device moves to another premise — WP-1.4's stamped
        // quantity-on-hand, applied to the figure a bill is raised from.
        builder.Property(reading => reading.Consumption)
            .HasColumnName("consumption")
            .HasPrecision(MeterReading.Precision, MeterReading.DecimalPlaces);

        builder.Property(reading => reading.RolledOver).HasColumnName("rolled_over").IsRequired();

        builder.Property(reading => reading.ExceptionCode)
            .HasColumnName("exception_code")
            .HasConversion<string>()
            .HasMaxLength(Meter.EnumNameLength)
            .IsRequired();

        builder.Property(reading => reading.CycleCode).HasColumnName("cycle_code").HasMaxLength(MeterReading.CycleCodeLength);
        builder.Property(reading => reading.Note).HasColumnName("note").HasMaxLength(MeterReading.NoteLength);
        builder.Property(reading => reading.ActorId).HasColumnName("actor_id").HasMaxLength(RegistryActor.MaxLength).IsRequired();
        builder.Property(reading => reading.ActorName).HasColumnName("actor_name").HasMaxLength(RegistryActor.MaxLength);
        builder.Property(reading => reading.RecordedAt).HasColumnName("recorded_at");

        // Derived from what is stored. Mapped, EF would want backing fields it cannot find and the
        // model would fail to build at startup rather than in a test.
        builder.Ignore(reading => reading.Days);
        builder.Ignore(reading => reading.DailyConsumption);
        builder.Ignore(reading => reading.IsException);

        // The reading belongs to the meter and goes when it goes — except that nothing in this
        // register is ever deleted, which is the point. Declared as a real foreign key because
        // unlike the premise, the meter is a row in this module's own schema.
        builder.HasOne<Meter>()
            .WithMany()
            .HasForeignKey(reading => reading.MeterId)
            .HasConstraintName("fk_meter_readings_meter")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(reading => reading.MeterId).HasDatabaseName("ix_meter_readings_meter_id");

        // "What did this premise use" is a query on this column across however many meters have
        // stood there, which is why the premise is stamped on the line at all.
        builder.HasIndex(reading => reading.ServiceLocationId).HasDatabaseName("ix_meter_readings_service_location_id");

        // The exception worklist: the screen that asks "what came back from this cycle that somebody
        // has to look at before it is billed".
        builder.HasIndex(reading => reading.ExceptionCode).HasDatabaseName("ix_meter_readings_exception_code");

        // ONE READING PER METER PER CYCLE, as a database fact.
        //
        // Unfiltered, and for the same reason ux_meters_service_location is: a manual reading holds
        // NULL in cycle_code, and NULLs in a unique index are distinct on both Postgres and the fast
        // tier's SQLite. So a premise can be re-read by hand as often as a dispute needs, while a
        // cycle run twice — the demo button pressed again, a retried request — collides instead of
        // quietly doubling every consumption figure it produced. No SQL predicate naming a column
        // for a later rename to desynchronise (WP-1.2's lesson).
        builder.HasIndex(reading => new { reading.MeterId, reading.CycleCode })
            .HasDatabaseName("ux_meter_readings_meter_cycle")
            .IsUnique();
    }
}
