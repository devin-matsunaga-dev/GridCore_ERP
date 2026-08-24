using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Billing.Features.RatePlans;

/// <summary>Maps <see cref="RatePlan"/> onto <c>billing.rate_plans</c> and seeds the shipped tariffs.</summary>
public sealed class RatePlanConfiguration : IEntityTypeConfiguration<RatePlan>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RatePlan> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("rate_plans");

        builder.HasKey(plan => plan.Id).HasName("pk_rate_plans");

        builder.Property(plan => plan.Id).HasColumnName("id");
        builder.Property(plan => plan.Code).HasColumnName("code").HasMaxLength(RatePlan.CodeLength).IsRequired();
        builder.Property(plan => plan.Name).HasColumnName("name").HasMaxLength(RatePlan.NameLength).IsRequired();

        builder.Property(plan => plan.ServiceType)
            .HasColumnName("service_type")
            .HasConversion<string>()
            .HasMaxLength(RatePlan.NameLength)
            .IsRequired();

        builder.Property(plan => plan.Currency).HasColumnName("currency").HasMaxLength(RatePlan.CurrencyLength).IsRequired();
        builder.Property(plan => plan.UnitOfMeasure).HasColumnName("unit_of_measure").HasMaxLength(RatePlan.UnitLength).IsRequired();

        // Money is decimal with an explicit scale, never the provider's default: a charge that
        // silently lost its cents would be found in a trial balance, months later.
        builder.Property(plan => plan.MonthlyServiceCharge).HasColumnName("monthly_service_charge").HasPrecision(18, 2);

        builder.Property(plan => plan.EffectiveFrom).HasColumnName("effective_from");
        builder.Property(plan => plan.IsDefault).HasColumnName("is_default");

        builder.HasMany(plan => plan.Tiers)
            .WithOne()
            .HasForeignKey(tier => tier.RatePlanId)
            .HasConstraintName("fk_rate_plan_tiers_rate_plans")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(plan => plan.Code).HasDatabaseName("ux_rate_plans_code").IsUnique();

        // "The default plan" cannot be two plans, so the database says so rather than the code
        // hoping so. Filtered, because every other plan is legitimately not the default.
        builder.HasIndex(plan => plan.IsDefault)
            .HasDatabaseName("ux_rate_plans_default")
            .IsUnique()
            .HasFilter("is_default");

        builder.HasData(DefaultRatePlans.All);
    }
}
