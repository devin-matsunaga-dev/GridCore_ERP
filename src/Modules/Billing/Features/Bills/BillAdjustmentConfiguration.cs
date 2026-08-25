using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Billing.Features.Bills;

/// <summary>Maps <see cref="BillAdjustment"/> onto <c>billing.bill_adjustments</c>.</summary>
public sealed class BillAdjustmentConfiguration : IEntityTypeConfiguration<BillAdjustment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<BillAdjustment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("bill_adjustments");

        builder.HasKey(adjustment => adjustment.Id).HasName("pk_bill_adjustments");

        // Never store-generated — WP-1.2's lesson, and this is exactly the shape it was about: an
        // adjustment appended to a tracked bill's collection is decided to be an insert or an update
        // by whether its key is already set.
        builder.Property(adjustment => adjustment.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(adjustment => adjustment.BillId).HasColumnName("bill_id");
        builder.Property(adjustment => adjustment.Sequence).HasColumnName("sequence");

        // Stored by name: a correction read back years from now must not depend on today's enum
        // ordering, which is what decides whether it was money on or money off.
        builder.Property(adjustment => adjustment.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(Bill.EnumNameLength)
            .IsRequired();

        // Money is decimal with an explicit scale, never the provider's default.
        builder.Property(adjustment => adjustment.Amount)
            .HasColumnName("amount")
            .HasPrecision(Bill.MoneyPrecision, Bill.MoneyScale);

        builder.Property(adjustment => adjustment.AmountDueAfter)
            .HasColumnName("amount_due_after")
            .HasPrecision(Bill.MoneyPrecision, Bill.MoneyScale);

        // Required in the database too, not merely in the aggregate. Invariant 5 is the whole point
        // of this table: a correction that moves money with nothing said about why is the row an
        // auditor comes looking for, and a nullable column is an invitation to write one.
        builder.Property(adjustment => adjustment.Reason)
            .HasColumnName("reason")
            .HasMaxLength(Bill.ReasonLength)
            .IsRequired();

        builder.Property(adjustment => adjustment.ActorId)
            .HasColumnName("actor_id")
            .HasMaxLength(RegistryActor.MaxLength)
            .IsRequired();

        builder.Property(adjustment => adjustment.ActorName)
            .HasColumnName("actor_name")
            .HasMaxLength(RegistryActor.MaxLength);

        builder.Property(adjustment => adjustment.RecordedAt).HasColumnName("recorded_at");

        // A bill cannot have two adjustment 2s. The order they were applied in is what makes
        // amount_due_after a column somebody can read down rather than a set of figures that
        // disagree — and it is what an ORDER BY can rely on when two rows share a millisecond.
        builder.HasIndex(adjustment => new { adjustment.BillId, adjustment.Sequence })
            .HasDatabaseName("ux_bill_adjustments_sequence")
            .IsUnique();
    }
}
