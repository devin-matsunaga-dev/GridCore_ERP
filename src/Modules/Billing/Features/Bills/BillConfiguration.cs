using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Billing.Features.Bills;

/// <summary>Maps <see cref="Bill"/> onto <c>billing.bills</c>.</summary>
public sealed class BillConfiguration : IEntityTypeConfiguration<Bill>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Bill> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("bills");

        builder.HasKey(bill => bill.Id).HasName("pk_bills");

        // Never store-generated — WP-1.2's lesson: with the Guid default, a freshly appended line
        // in the Lines collection is tracked as Modified and the save throws having updated nothing.
        builder.Property(bill => bill.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(bill => bill.BillNumber)
            .HasColumnName("bill_number")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        // No foreign keys on any of these three: Customers is another module over another schema.
        // The ids come from IServiceAccountDirectory, which has already proved they exist.
        builder.Property(bill => bill.ServiceAccountId).HasColumnName("service_account_id");
        builder.Property(bill => bill.CustomerId).HasColumnName("customer_id");
        builder.Property(bill => bill.ServiceLocationId).HasColumnName("service_location_id");

        // Stamped so a bill reads on its own, without three cross-module lookups that would answer
        // differently after a customer is renamed.
        builder.Property(bill => bill.AccountNumber)
            .HasColumnName("account_number")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        builder.Property(bill => bill.CustomerName)
            .HasColumnName("customer_name")
            .HasMaxLength(Bill.NameLength)
            .IsRequired();

        // The tariff version priced against, stamped for the same reason. No foreign key to
        // rate_plans either — not because of a module boundary, but because a bill must survive a
        // tariff being superseded, and a real key invites a cascade nobody wants near a document.
        builder.Property(bill => bill.RatePlanId).HasColumnName("rate_plan_id");

        builder.Property(bill => bill.RatePlanCode)
            .HasColumnName("rate_plan_code")
            .HasMaxLength(RatePlans.RatePlan.CodeLength)
            .IsRequired();

        builder.Property(bill => bill.RatePlanName)
            .HasColumnName("rate_plan_name")
            .HasMaxLength(Bill.NameLength)
            .IsRequired();

        builder.Property(bill => bill.RatePlanEffectiveFrom).HasColumnName("rate_plan_effective_from");

        builder.Property(bill => bill.Currency)
            .HasColumnName("currency")
            .HasMaxLength(RatePlans.RatePlan.CurrencyLength)
            .IsRequired();

        builder.Property(bill => bill.UnitOfMeasure)
            .HasColumnName("unit_of_measure")
            .HasMaxLength(RatePlans.RatePlan.UnitLength)
            .IsRequired();

        builder.Property(bill => bill.PeriodStart).HasColumnName("period_start");
        builder.Property(bill => bill.PeriodEnd).HasColumnName("period_end");
        builder.Property(bill => bill.CycleCode).HasColumnName("cycle_code").HasMaxLength(Bill.CycleCodeLength);

        // What the bill was raised from, in Metering's register. Stamped, not resolved: the meter's
        // register width may since have been corrected and the device may be on another wall.
        builder.Property(bill => bill.MeterReadingId).HasColumnName("meter_reading_id");
        builder.Property(bill => bill.MeterId).HasColumnName("meter_id");

        builder.Property(bill => bill.MeterNumber)
            .HasColumnName("meter_number")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        builder.Property(bill => bill.PreviousReading)
            .HasColumnName("previous_reading")
            .HasPrecision(Bill.QuantityPrecision, Bill.QuantityScale);

        builder.Property(bill => bill.CurrentReading)
            .HasColumnName("current_reading")
            .HasPrecision(Bill.QuantityPrecision, Bill.QuantityScale);

        builder.Property(bill => bill.Consumption)
            .HasColumnName("consumption")
            .HasPrecision(Bill.QuantityPrecision, Bill.QuantityScale);

        // Money is decimal with an explicit scale, never the provider's default.
        builder.Property(bill => bill.TotalAmount)
            .HasColumnName("total_amount")
            .HasPrecision(Bill.MoneyPrecision, Bill.MoneyScale);

        builder.Property(bill => bill.AmountPaid)
            .HasColumnName("amount_paid")
            .HasPrecision(Bill.MoneyPrecision, Bill.MoneyScale);

        // Signed, and stored rather than derived: a list does not load a bill's adjustments, and a
        // register page that reported what was printed as what is owed would be wrong on every
        // corrected bill. Bill.Adjust checks this against the loaded history before adding to it.
        builder.Property(bill => bill.AdjustmentTotal)
            .HasColumnName("adjustment_total")
            .HasPrecision(Bill.MoneyPrecision, Bill.MoneyScale);

        // Stored by name: a bill read back years from now must not depend on today's enum ordering.
        builder.Property(bill => bill.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(Bill.EnumNameLength)
            .IsRequired();

        builder.Property(bill => bill.CreatedAt).HasColumnName("created_at");
        builder.Property(bill => bill.IssuedOn).HasColumnName("issued_on");
        builder.Property(bill => bill.DueDate).HasColumnName("due_date");
        builder.Property(bill => bill.PaidAt).HasColumnName("paid_at");
        builder.Property(bill => bill.StatusChangedAt).HasColumnName("status_changed_at");
        builder.Property(bill => bill.StatusReason).HasColumnName("status_reason").HasMaxLength(Bill.ReasonLength);
        builder.Property(bill => bill.ActorId).HasColumnName("actor_id").HasMaxLength(RegistryActor.MaxLength).IsRequired();
        builder.Property(bill => bill.ActorName).HasColumnName("actor_name").HasMaxLength(RegistryActor.MaxLength);

        // Derived from what is stored. Mapped, EF would want backing fields it cannot find and the
        // model would fail to build at startup rather than in a test.
        builder.Ignore(bill => bill.AmountDue);
        builder.Ignore(bill => bill.Balance);
        builder.Ignore(bill => bill.IsOutstanding);
        builder.Ignore(bill => bill.AllowedTransitions);

        // Lines ARE a navigation, unlike the readings hanging off a meter (WP-2.2). A bill has a
        // handful of them, they are always read with it, and there is no path that loads a bill and
        // does not want them.
        builder.HasMany(bill => bill.Lines)
            .WithOne()
            .HasForeignKey(line => line.BillId)
            .HasConstraintName("fk_bill_lines_bill")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Bill.Lines))!.SetPropertyAccessMode(PropertyAccessMode.Field);

        // The corrections made since. A navigation like the lines, and for the same reasons: there
        // are a handful of them at most, and no path loads a bill to decide what is owed without
        // wanting them — Bill.Adjust refuses to run without them at all.
        builder.HasMany(bill => bill.Adjustments)
            .WithOne()
            .HasForeignKey(adjustment => adjustment.BillId)
            .HasConstraintName("fk_bill_adjustments_bill")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Bill.Adjustments))!.SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(bill => bill.BillNumber).HasDatabaseName("ux_bills_bill_number").IsUnique();
        builder.HasIndex(bill => bill.CustomerId).HasDatabaseName("ix_bills_customer_id");
        builder.HasIndex(bill => bill.Status).HasDatabaseName("ix_bills_status");

        // ONE BILL PER ACCOUNT PER CYCLE, as a database fact.
        //
        // Unfiltered, and for the reason ux_meter_readings_meter_cycle is (WP-2.2): an ad-hoc bill
        // holds NULL in cycle_code, and NULLs in a unique index are distinct on both Postgres and
        // the fast tier's SQLite. So an account can be billed by hand as often as a correction needs
        // — a final bill, a re-bill after a dispute — while a billing run over a cycle that has
        // already been billed cannot double anybody's charges. No SQL predicate naming a column for
        // a later rename to desynchronise (WP-1.2's lesson).
        //
        // The service also pre-checks it, but unlike the reading cycle it does NOT refuse the whole
        // run: an account already billed is skipped and named in the result, because re-running a
        // cycle after clearing its exception worklist is ordinary work rather than a mistake.
        builder.HasIndex(bill => new { bill.ServiceAccountId, bill.CycleCode })
            .HasDatabaseName("ux_bills_account_cycle")
            .IsUnique();
    }
}
