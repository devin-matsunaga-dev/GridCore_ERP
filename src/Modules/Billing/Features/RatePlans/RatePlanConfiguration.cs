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

        // Derived from the code and the effective date. Mapped, EF would want a backing field it
        // cannot find and the model would fail to build at startup rather than in a test.
        builder.Ignore(plan => plan.VersionKey);

        builder.HasMany(plan => plan.Tiers)
            .WithOne()
            .HasForeignKey(tier => tier.RatePlanId)
            .HasConstraintName("fk_rate_plan_tiers_rate_plans")
            .OnDelete(DeleteBehavior.Cascade);

        // A CODE AND A DATE, not a code (WP-2.3). A tariff is republished whenever its prices
        // change and the versions have to coexist, so what cannot be duplicated is one code taking
        // effect twice on the same day. Unique on the code alone — which is what WP-0.8 shipped,
        // when there was one version of each — would make repricing impossible.
        builder.HasIndex(plan => new { plan.Code, plan.EffectiveFrom })
            .HasDatabaseName("ux_rate_plans_code_effective")
            .IsUnique();

        // "The default plan" cannot be two plans ON ONE DAY FOR ONE SERVICE, so the database says so
        // rather than the code hoping so. Filtered, because every other plan is legitimately not the
        // default; keyed on the effective date as well, because every version of the default tariff
        // carries the flag — repricing the default must not leave the utility without one.
        //
        // The SERVICE joined the key in WP-2.17. Keyed on the flag and the date alone, which is what
        // WP-2.3 shipped when every tariff was an electric one, a default water tariff could never be
        // published at all: it would collide with the electric default on its own effective date.
        builder.HasIndex(plan => new { plan.ServiceType, plan.IsDefault, plan.EffectiveFrom })
            .HasDatabaseName("ux_rate_plans_default_effective")
            .IsUnique()
            .HasFilter("is_default");

        builder.HasData(DefaultRatePlans.All);
    }
}
