using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Billing.Features.Bills;

/// <summary>Maps <see cref="BillLine"/> onto <c>billing.bill_lines</c>.</summary>
public sealed class BillLineConfiguration : IEntityTypeConfiguration<BillLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<BillLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("bill_lines");

        builder.HasKey(line => line.Id).HasName("pk_bill_lines");

        // Never store-generated — this is exactly the entity WP-1.2's lesson was about: a line
        // appended to a tracked bill's collection is decided to be an insert or an update by whether
        // its key is set on a store-generated column.
        builder.Property(line => line.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(line => line.BillId).HasColumnName("bill_id");
        builder.Property(line => line.Sequence).HasColumnName("sequence");

        builder.Property(line => line.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(BillLine.EnumNameLength)
            .IsRequired();

        builder.Property(line => line.Description)
            .HasColumnName("description")
            .HasMaxLength(BillLine.DescriptionLength)
            .IsRequired();

        builder.Property(line => line.TierSequence).HasColumnName("tier_sequence");

        builder.Property(line => line.Units)
            .HasColumnName("units")
            .HasPrecision(Bill.QuantityPrecision, BillLine.UnitsDecimalPlaces);

        // The rate is stamped at the tariff's own scale, not money's: a unit price of 0.1145 rounded
        // to the cent would be 0.11, and the line would no longer explain its own amount.
        builder.Property(line => line.RatePerUnit)
            .HasColumnName("rate_per_unit")
            .HasPrecision(Bill.QuantityPrecision, BillLine.RateDecimalPlaces);

        builder.Property(line => line.Amount)
            .HasColumnName("amount")
            .HasPrecision(Bill.MoneyPrecision, Bill.MoneyScale);

        // A bill cannot have two line 3s — the order the lines are printed in is the bill.
        builder.HasIndex(line => new { line.BillId, line.Sequence })
            .HasDatabaseName("ux_bill_lines_sequence")
            .IsUnique();
    }
}
