using GridCore.Contracts.Directories;
using GridCore.Modules.Metering.Data;
using GridCore.Modules.Metering.Features.Meters;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Metering.Features.Readings;

/// <summary>
/// Metering's answer to <see cref="IMeterReadingDirectory"/>: the reading register as the rest of
/// GridCore is allowed to see it.
/// </summary>
/// <remarks>
/// <para>
/// Registered by <see cref="MeteringModule"/> — the only place that knows both halves — and shaped
/// exactly like the premise directory Customers registers. Billing (WP-2.3) bills from readings and
/// may neither reference this module nor read <c>metering.meter_readings</c>.
/// </para>
/// <para>
/// The meter <b>number</b> is joined on here rather than left to the caller. It is printed on every
/// bill raised from a reading, and a caller that had to fetch it separately would be a caller
/// holding a meter id and a reason to ask for a meter directory as well — two seams where the
/// question is really one.
/// </para>
/// <para>
/// Read-only, for the reason the other two directories are: recording a reading, running a cycle
/// and clearing an exception stay behind <c>IMeterReadingService</c> inside Metering. A module that
/// could write a reading is a module that could raise a bill from a figure the register never saw.
/// </para>
/// </remarks>
public sealed class MeterReadingDirectory(MeteringDbContext database) : IMeterReadingDirectory
{
    /// <summary>The largest page a lookup will answer, whatever the caller asks for.</summary>
    public const int MaxPageSize = MeterReadingService.MaxPageSize;

    /// <summary>
    /// Most readings one cycle lookup will return. A cycle is capped at
    /// <see cref="MeterReadingService.MaxCycleSize"/> readings when it is run, so a caller asking
    /// for all of one can be answered in a single page.
    /// </summary>
    public const int MaxCycleSize = MeterReadingService.MaxCycleSize;

    /// <inheritdoc />
    public async Task<MeterReadingSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var found = await Summaries(Readings().Where(reading => reading.Id == id))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return found is null ? null : Summarise(found);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MeterReadingSummary>> ForCycleAsync(
        string cycleCode,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleCode);

        var cycle = cycleCode.Trim();

        var found = await Summaries(
                Readings()
                    .Where(reading => reading.CycleCode == cycle)

                    // Oldest first, unlike every other list in GridCore: a billing run walks a cycle
                    // in the order it was read, and a caller that has to reverse a page before using
                    // it is a caller one refactor away from billing a cycle backwards.
                    .OrderBy(reading => reading.Id)
                    .Take(Math.Clamp(limit, 1, MaxCycleSize)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return found.ConvertAll(Summarise);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MeterReadingSummary>> AtLocationAsync(
        Guid serviceLocationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var found = await Summaries(
                Readings()
                    .Where(reading => reading.ServiceLocationId == serviceLocationId)

                    // Ordered by key: ids are Guid v7, so the primary-key index already orders
                    // chronologically on Postgres and on the fast tier's SQLite alike.
                    .OrderByDescending(reading => reading.Id)
                    .Take(Math.Clamp(limit, 1, MaxPageSize)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return found.ConvertAll(Summarise);
    }

    /// <summary>Every reading, untracked. A caller outside this module has no business holding one.</summary>
    private IQueryable<MeterReading> Readings() => database.MeterReadings.AsNoTracking();

    /// <summary>
    /// Joins <paramref name="readings"/> to the meters that produced them. A join rather than a
    /// navigation, because WP-2.2 deliberately kept readings off <see cref="Meter"/>: recording one
    /// reading must not load a decade of them.
    /// </summary>
    /// <remarks>
    /// Takes the filtered and ordered readings rather than filtering afterwards: EF cannot translate
    /// a <c>Where</c> applied to a projection into a record, so a query that projected first would
    /// build fine and throw at run time.
    /// </remarks>
    private IQueryable<ReadingRow> Summaries(IQueryable<MeterReading> readings) =>
        from reading in readings
        join meter in database.Meters.AsNoTracking() on reading.MeterId equals meter.Id
        select new ReadingRow(reading, meter.MeterNumber);

    private static MeterReadingSummary Summarise(ReadingRow row) =>
        new(
            row.Reading.Id,
            row.Reading.MeterId,
            row.MeterNumber,
            row.Reading.ServiceLocationId,
            row.Reading.ReadingDate,
            row.Reading.Reading,
            row.Reading.PreviousReading,
            row.Reading.PreviousReadingDate,
            row.Reading.Consumption,

            // By name, never the enum: Contracts takes no dependency on this module's types.
            row.Reading.ExceptionCode.ToString(),
            row.Reading.IsException,
            row.Reading.CycleCode);

    /// <summary>One reading and the number of the meter it came off, as the join hands them over.</summary>
    private sealed record ReadingRow(MeterReading Reading, string MeterNumber);
}
