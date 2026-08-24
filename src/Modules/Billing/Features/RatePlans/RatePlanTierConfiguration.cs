using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Billing.Features.RatePlans;

/// <summary>Maps <see cref="RatePlanTier"/> onto <c>billing.rate_plan_tiers</c> and seeds the shipped tiers.</summary>
public sealed class RatePlanTierConfiguration : IEntityTypeConfiguration<RatePlanTier>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RatePlanTier> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("rate_plan_tiers");

        builder.HasKey(tier => tier.Id).HasName("pk_rate_plan_tiers");

        builder.Property(tier => tier.Id).HasColumnName("id");
        builder.Property(tier => tier.RatePlanId).HasColumnName("rate_plan_id");
        builder.Property(tier => tier.Sequence).HasColumnName("sequence");

        // Consumption bounds and unit prices are decimal for the same reason money is: a tariff is
        // arithmetic that must come out the same every time it is run.
        builder.Property(tier => tier.UpToUnits).HasColumnName("up_to_units").HasPrecision(18, 3);
        builder.Property(tier => tier.RatePerUnit).HasColumnName("rate_per_unit").HasPrecision(18, 6);

        // A plan cannot have two tier 2s — the order tiers are applied in is the tariff.
        builder.HasIndex(tier => new { tier.RatePlanId, tier.Sequence })
            .HasDatabaseName("ux_rate_plan_tiers_sequence")
            .IsUnique();

        builder.HasData(DefaultRatePlans.AllTiers);
    }
}
