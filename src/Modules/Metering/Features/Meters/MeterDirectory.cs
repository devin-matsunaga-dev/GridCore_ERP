using GridCore.Contracts.Directories;
using GridCore.Modules.Metering.Data;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Metering.Features.Meters;

/// <summary>
/// Metering's answer to <see cref="IMeterDirectory"/>: the meter register as the rest of GridCore
/// is allowed to see it.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="Readings.MeterReadingDirectory"/>, registered by
/// <see cref="MeteringModule"/> — the only place that knows both halves. Customers (WP-2.9) has to
/// turn the number a caller reads off their wall into the premise it is measuring, and may neither
/// reference this module nor read <c>metering.meters</c>.
/// </para>
/// <para>
/// Both lookups are <c>AsNoTracking</c> and both project to <see cref="MeterSummary"/>. Neither
/// touches the history collection: a caller outside this module has no business holding a
/// <see cref="Meter"/>, and a projection is what guarantees it cannot.
/// </para>
/// </remarks>
public sealed class MeterDirectory(MeteringDbContext database) : IMeterDirectory
{
    /// <summary>The largest candidate set a search will answer, whatever the caller asks for.</summary>
    public const int MaxPageSize = MeterService.MaxPageSize;

    /// <inheritdoc />
    public async Task<MeterSummary?> FindByNumberAsync(string meterNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meterNumber);

        // Lower-cased on both sides rather than ILIKE, which is Npgsql-only and would leave the fast
        // tier exercising different SQL than production runs (WP-1.1's rule). Equality rather than a
        // contains, so this stays a seek on ux_meters_meter_number.
        var wanted = meterNumber.Trim().ToLowerInvariant();

        var found = await Meters()
            .Where(meter => meter.MeterNumber.ToLower() == wanted)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return found is null ? null : Summarise(found);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MeterSummary>> SearchByNumberAsync(
        string term,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);

        var wanted = term.Trim().ToLowerInvariant();

        // A contains, so no index helps and the cap is what bounds it. That is the honest cost of
        // matching a half-remembered number, and it is why the caller probes for an exact one first.
        var found = await Meters()
            .Where(meter => meter.MeterNumber.ToLower().Contains(wanted))
            .OrderBy(meter => meter.MeterNumber)
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return found.Select(Summarise).ToList();
    }

    /// <summary>Every meter, untracked. A caller outside this module has no business holding one.</summary>
    private IQueryable<Meter> Meters() => database.Meters.AsNoTracking();

    private static MeterSummary Summarise(Meter meter) =>
        new(
            meter.Id,
            meter.MeterNumber,
            meter.SerialNumber,

            // By name, never the enum: Contracts takes no dependency on this module's types.
            meter.Type.ToString(),
            meter.Status.ToString(),
            meter.ServiceLocationId,
            meter.IsFitted);
}
