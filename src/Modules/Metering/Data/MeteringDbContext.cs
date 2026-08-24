using GridCore.Modules.Metering.Features.Meters;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Metering.Data;

/// <summary>
/// The Metering module's schema: the utility's revenue meters and everywhere each of them has been.
/// </summary>
public sealed class MeteringDbContext(DbContextOptions<MeteringDbContext> options) : DbContext(options)
{
    /// <summary>The Postgres schema this context owns — also the module's name.</summary>
    public const string SchemaName = "metering";

    /// <summary>The utility's revenue meters.</summary>
    public DbSet<Meter> Meters => Set<Meter>();

    /// <summary>
    /// Every line of every meter's history. Exposed as a set of its own so one meter's history can
    /// be read without loading the meter, which is what the history endpoint does.
    /// </summary>
    public DbSet<MeterHistoryEntry> MeterHistory => Set<MeterHistoryEntry>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MeteringDbContext).Assembly);
    }
}
