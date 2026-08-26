using GridCore.Contracts.Directories;
using GridCore.Contracts.Services;
using GridCore.Modules.Metering.Data;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Metering.Features.Readings;

/// <summary>
/// Metering's answer to <see cref="IUsageDirectory"/>: what a premise has been consuming, as the
/// rest of GridCore is allowed to see it (WP-2.17).
/// </summary>
/// <remarks>
/// <para>
/// Registered by <see cref="MeteringModule"/> — the only place that knows both halves — and shaped
/// like <see cref="MeterReadingDirectory"/> beside it. Customers assesses a usage-based deposit
/// through this and may neither reference this module nor read <c>metering.meter_readings</c>.
/// </para>
/// <para>
/// <b>The rows are fetched here and the arithmetic is done in <see cref="UsageAverage"/>.</b> This
/// class decides <i>which</i> readings count; that one decides what they come to. Splitting them is
/// what lets every case of the average — a missed cycle, a single period, no history at all — be
/// proven with no database, and it leaves this file with one job a reader can check by eye.
/// </para>
/// <para>
/// <b>By premise, across whatever meters have stood there.</b> "What has this place been using" is a
/// question about the premise and not about the device — the same call
/// <see cref="MeterReadingDirectory.AtLocationAsync"/> makes, and for the same reason: a meter
/// exchanged last March must not halve the history of the wall it was on.
/// </para>
/// </remarks>
public sealed class UsageDirectory(MeteringDbContext database) : IUsageDirectory
{
    /// <summary>Most periods one average will ever be drawn from, whatever the caller asks for.</summary>
    /// <remarks>
    /// Twenty-four monthly cycles: two years, which is longer than any deposit basis GridCore
    /// publishes and short enough that the query stays a bounded read on
    /// <c>ix_meter_readings_service_location_id</c>.
    /// </remarks>
    public const int MaxPeriods = 24;

    /// <summary>
    /// The one service this module holds a meter register for.
    /// </summary>
    /// <remarks>
    /// <b>Stated here rather than assumed by the query.</b> Every meter in <c>metering.meters</c> is
    /// an electricity meter — <c>MeterType</c> lists single-phase, three-phase, CT and demand
    /// arrangements and nothing else — so a reading at a premise is an electricity reading, and
    /// answering a question about that premise's <i>water</i> with it would hand a caller kWh
    /// labelled as cubic metres. A water register is the billing-deepening pass's, and the day it
    /// arrives this is the line that changes.
    /// </remarks>
    public const ServiceType MeasuredService = ServiceType.Electricity;

    /// <inheritdoc />
    public async Task<PremiseUsage> AverageMonthlyAtLocationAsync(
        Guid serviceLocationId,
        ServiceType serviceType,
        int periods,
        CancellationToken cancellationToken = default)
    {
        // No history rather than an error, and rather than the electricity figure. The caller asked
        // a reasonable question about a supply this deployment does not meter; "nothing measured" is
        // the true answer, and it is the one that makes a usage-based deposit fall back to its
        // published minimum instead of quietly pricing the wrong units.
        if (serviceType != MeasuredService)
        {
            return PremiseUsage.None(serviceLocationId);
        }

        // A reading with no consumption figure is skipped rather than counted as zero: a missed
        // read, one still on the exception worklist and the first read after an installation all
        // land here, and none of them is evidence that a premise used nothing that month. Nor is a
        // reading with no previous date — there is no period to divide by.
        var measured = await database.MeterReadings
            .AsNoTracking()
            .Where(reading => reading.ServiceLocationId == serviceLocationId)
            .Where(reading => reading.Consumption != null && reading.PreviousReadingDate != null)

            // Newest first, so a cap takes the most recent periods rather than the oldest ones — a
            // deposit is assessed on what the premise uses now. Ordered by KEY rather than by the
            // reading date: ids are Guid v7, so the primary-key index already orders chronologically
            // on Postgres and on the fast tier's SQLite alike, and a DateTimeOffset comparison is
            // one of the few things SQLite cannot translate at all.
            .OrderByDescending(reading => reading.Id)
            .Take(Math.Clamp(periods, 1, MaxPeriods))
            .Select(reading => new
            {
                reading.Consumption,
                reading.PreviousReadingDate,
                reading.ReadingDate,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (measured.Count is 0)
        {
            return PremiseUsage.None(serviceLocationId);
        }

        var usage = measured.ConvertAll(row =>
            new UsagePeriod(row.Consumption!.Value, row.PreviousReadingDate!.Value, row.ReadingDate));

        var average = UsageAverage.MonthlyOf(usage);

        // Null when every period the query returned had a non-positive span, which the arithmetic
        // refuses to divide by. There is still no history to report, so this reads as none.
        return average is null
            ? PremiseUsage.None(serviceLocationId)
            : new PremiseUsage(
                serviceLocationId,
                average,
                usage.Count,
                UsageAverage.DaysCoveredBy(usage),
                usage.Min(period => period.Start),
                usage.Max(period => period.End));
    }
}
