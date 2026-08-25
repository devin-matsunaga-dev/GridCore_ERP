using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.RatePlans;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.Data;

/// <summary>
/// The Billing module's schema: the published tariffs, who is billed on which of them, the bills
/// the rate engine produces, and the corrections made to those.
/// </summary>
public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    /// <summary>The Postgres schema this context owns — also the module's name.</summary>
    public const string SchemaName = "billing";

    /// <summary>
    /// Every version of every published tariff. A tariff is republished when its prices change, so
    /// several rows share a code and are told apart by <see cref="RatePlan.EffectiveFrom"/>.
    /// </summary>
    public DbSet<RatePlan> RatePlans => Set<RatePlan>();

    /// <summary>The consumption tiers of those tariffs.</summary>
    public DbSet<RatePlanTier> RatePlanTiers => Set<RatePlanTier>();

    /// <summary>
    /// Which tariff each service account is billed on. An account with no row here bills on the
    /// default tariff, so nothing has to be assigned before the utility can bill at all.
    /// </summary>
    public DbSet<AccountRatePlan> AccountRatePlans => Set<AccountRatePlan>();

    /// <summary>
    /// Every bill raised. Append-only in spirit: a bill's lines are written once and a correction is
    /// a new document or an adjustment, never a rewritten line.
    /// </summary>
    public DbSet<Bill> Bills => Set<Bill>();

    /// <summary>
    /// Every line of every bill. Exposed as a set of its own so a total can be reconciled against
    /// its lines without loading the bills.
    /// </summary>
    public DbSet<BillLine> BillLines => Set<BillLine>();

    /// <summary>
    /// Every correction made to a bill since it was issued. Append-only for real, not merely in
    /// spirit: an adjustment is never edited or removed, and what a customer owes today is the
    /// bill's printed total plus the sum of these.
    /// </summary>
    public DbSet<BillAdjustment> BillAdjustments => Set<BillAdjustment>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BillingDbContext).Assembly);
    }
}
