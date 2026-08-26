using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Fees;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Billing.Features.Delinquency;

/// <summary>Maps <see cref="LateChargeAssessment"/> onto <c>billing.late_charge_assessments</c>.</summary>
public sealed class LateChargeAssessmentConfiguration : IEntityTypeConfiguration<LateChargeAssessment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LateChargeAssessment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("late_charge_assessments");

        builder.HasKey(assessment => assessment.Id).HasName("pk_late_charge_assessments");

        builder.Property(assessment => assessment.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(assessment => assessment.BillId).HasColumnName("bill_id");

        builder.Property(assessment => assessment.BillNumber)
            .HasColumnName("bill_number")
            .HasMaxLength(LateChargeAssessment.NumberLength)
            .IsRequired();

        builder.Property(assessment => assessment.ServiceAccountId).HasColumnName("service_account_id");

        builder.Property(assessment => assessment.AccountNumber)
            .HasColumnName("account_number")
            .HasMaxLength(LateChargeAssessment.NumberLength)
            .IsRequired();

        builder.Property(assessment => assessment.CustomerId).HasColumnName("customer_id");

        builder.Property(assessment => assessment.PeriodStart).HasColumnName("period_start");
        builder.Property(assessment => assessment.AssessedOn).HasColumnName("assessed_on");
        builder.Property(assessment => assessment.DaysPastDue).HasColumnName("days_past_due");

        // Money is decimal with an explicit scale, never the provider's default.
        builder.Property(assessment => assessment.BasisAmount)
            .HasColumnName("basis_amount")
            .HasPrecision(Bill.MoneyPrecision, Bill.MoneyScale);

        // A rate, not money — four decimal places, as the schedule row it came from.
        builder.Property(assessment => assessment.Rate)
            .HasColumnName("rate")
            .HasPrecision(FeeScheduleEntry.RatePrecision, FeeScheduleEntry.RateDecimalPlaces);

        builder.Property(assessment => assessment.Amount)
            .HasColumnName("amount")
            .HasPrecision(Bill.MoneyPrecision, Bill.MoneyScale);

        builder.Property(assessment => assessment.Currency)
            .HasColumnName("currency")
            .HasMaxLength(FeeScheduleEntry.CurrencyLength)
            .IsRequired();

        builder.Property(assessment => assessment.FeeScheduleId).HasColumnName("fee_schedule_id");
        builder.Property(assessment => assessment.AccountChargeId).HasColumnName("account_charge_id");
        builder.Property(assessment => assessment.AssessedAt).HasColumnName("assessed_at");

        builder.Property(assessment => assessment.ActorId)
            .HasColumnName("actor_id")
            .HasMaxLength(GridCore.Platform.Registry.RegistryActor.MaxLength)
            .IsRequired();

        builder.Property(assessment => assessment.ActorName)
            .HasColumnName("actor_name")
            .HasMaxLength(GridCore.Platform.Registry.RegistryActor.MaxLength);

        // THE IDEMPOTENCY. One late charge per bill per month, enforced by the database rather than
        // by the run having looked first: two runs racing each other both find nothing and both try
        // to insert, and this is what makes the second of them fail instead of charging the customer
        // twice. WORK_PACKAGES.md asks for exactly this by name.
        builder.HasIndex(assessment => new { assessment.BillId, assessment.PeriodStart })
            .HasDatabaseName("ux_late_charge_assessments_bill_period")
            .IsUnique();

        // The register read: "what has this account been charged for being late", newest first.
        builder.HasIndex(assessment => new { assessment.ServiceAccountId, assessment.PeriodStart })
            .HasDatabaseName("ix_late_charge_assessments_account_period");

        // No foreign key to billing.bills or billing.account_charges, the call AccountCharge already
        // makes about the fee schedule: this row is a record of what a run did, and it has to keep
        // saying so whatever later happens to the documents it names.
    }
}
