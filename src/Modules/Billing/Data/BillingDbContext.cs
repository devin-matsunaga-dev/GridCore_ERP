using GridCore.Modules.Billing.Features.RatePlans;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.Data;

/// <summary>
/// The Billing module's schema. Today it holds the published tariffs; WP-2.3 adds the bills the
/// rate engine produces from them and WP-2.4 the adjustments made to those.
/// </summary>
public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    /// <summary>The Postgres schema this context owns — also the module's name.</summary>
    public const string SchemaName = "billing";

    /// <summary>The published tariffs.</summary>
    public DbSet<RatePlan> RatePlans => Set<RatePlan>();

    /// <summary>The consumption tiers of those tariffs.</summary>
    public DbSet<RatePlanTier> RatePlanTiers => Set<RatePlanTier>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BillingDbContext).Assembly);
    }
}
