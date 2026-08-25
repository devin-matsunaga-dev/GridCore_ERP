using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
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

    /// <summary>
    /// Every reading ever taken off every meter. Append-only, and deliberately not a navigation
    /// collection on the meter: recording one reading must not load a decade of them, and the
    /// register that WP-2.3's bills are raised from is the one that will grow fastest in GridCore.
    /// </summary>
    public DbSet<MeterReading> MeterReadings => Set<MeterReading>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MeteringDbContext).Assembly);
    }
}
