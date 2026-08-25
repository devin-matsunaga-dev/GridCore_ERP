using System.Globalization;
using GridCore.Contracts.Providers;
using GridCore.Modules.Metering.Data;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Platform.Registry;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Metering.Seeding;

/// <summary>
/// A year of reading cycles across the demo utility's fitted meters, so the register opens with
/// consumption to look at, a worklist with real exceptions on it, and something for WP-2.3 to bill.
/// </summary>
/// <remarks>
/// <para>
/// Every reading here comes out of the real <see cref="IMeterReadingProvider"/> and through the real
/// <see cref="MeterReading.Record"/>. Nothing is assigned: consumption, rollover and every exception
/// code are worked out by the same code a live cycle run uses, which means an impossible demo
/// reading fails at startup naming the meter rather than shipping a figure nothing explains. Same
/// call <see cref="MetersDemoSeeder"/> makes by walking the aggregate's transitions.
/// </para>
/// <para>
/// The seed is fixed, so the demo world is the same on every developer's machine and in CI: the same
/// meters come back unread, the same premise comes back using far too much. That is the point of the
/// provider taking a seed at all.
/// </para>
/// <para>
/// A seeder of its own rather than more rows in <see cref="MetersDemoSeeder"/>, for WP-1.2's two
/// reasons: <see cref="Name"/> is the dedupe key, so extending a seeder that has already run seeds
/// nothing — and each seeder gets its own unit of work, which is what lets this one query the meters
/// the previous one committed.
/// </para>
/// </remarks>
public sealed class MeterReadingsDemoSeeder(
    MeteringDbContext database,
    IMeterReadingProvider provider,
    TimeProvider clock) : IDemoSeeder
{
    /// <summary>
    /// The seed the demo world's readings are generated from. Fixed forever: changing it reshuffles
    /// which meters carry exceptions, and every screenshot and test that names one goes with it.
    /// </summary>
    public const int Seed = 4471;

    /// <summary>How many monthly cycles are seeded, ending with last month.</summary>
    public const int Cycles = 12;

    /// <summary>Hour of the first of the month a demo cycle is read at.</summary>
    private const int ReadHourUtc = 9;

    /// <summary>Who the seeded readings are attributed to — the same stand-in colleague who fits the meters.</summary>
    private static RegistryActor Attribution { get; } = RegistryActor.Of(MetersDemoSeeder.Fitter);

    /// <inheritdoc />
    /// <remarks>The dedupe key. Never renamed — a rename seeds a second year of readings.</remarks>
    public string Name => "metering.readings";

    /// <inheritdoc />
    /// <remarks>After the meter register (600), whose fitted meters this one reads.</remarks>
    public int Order => 700;

    /// <inheritdoc />
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        // Only fitted meters are on a route — the column the aggregate keeps in step with the
        // status, exactly as the live cycle asks for them.
        var meters = await database.Meters
            .Where(meter => meter.ServiceLocationId != null)
            .OrderBy(meter => meter.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (meters.Count is 0)
        {
            // Nothing fitted, nothing to read. Not an error: a database seeded with no customers has
            // no premises, so it has no meters either.
            return;
        }

        var now = clock.GetUtcNow();

        // Ids are Guid v7 stamped from the instant they are created, and rows created in the same
        // instant have no defined order. A step per reading keeps the register — which is ordered by
        // key — in the order the cycles were actually read.
        var step = 0;

        DateTimeOffset Next() => now.AddMilliseconds(step++);

        // The premise's readings, newest first, exactly as the service's baseline query hands them
        // over. Held in memory because none of these rows is visible to a query until the runner's
        // unit of work commits.
        var atPremise = new Dictionary<Guid, List<MeterReading>>();

        foreach (var readAt in CycleDates(now))
        {
            var cycleCode = readAt.ToString("yyyy-MM", CultureInfo.InvariantCulture);

            var route = meters
                .Select(meter => Describe(meter, Baseline(meter, atPremise)))
                .ToList();

            var batch = await provider
                .ReadCycleAsync(new MeterReadingCycle(cycleCode, readAt, Seed, route), cancellationToken)
                .ConfigureAwait(false);

            var byId = meters.ToDictionary(meter => meter.Id);

            foreach (var result in batch.Readings)
            {
                if (!byId.TryGetValue(result.MeterId, out var meter) || meter.ServiceLocationId is not { } premise)
                {
                    continue;
                }

                var reading = MeterReading.Record(
                    meter,
                    Baseline(meter, atPremise),
                    result.Reading,
                    result.ReadAt,
                    MeterReadingSource.Cycle,
                    Attribution,
                    Next(),
                    cycleCode,
                    result.Note);

                database.MeterReadings.Add(reading);

                // Newest first, which is the order ReadingBaseline.From reads.
                atPremise.TryAdd(premise, []);
                atPremise[premise].Insert(0, reading);
            }
        }

        // No SaveChanges: the runner's unit of work saves these and the seed record in one
        // transaction, which is what makes a half-read demo cycle impossible.
    }

    /// <summary>
    /// The first of each of the last <see cref="Cycles"/> complete months, oldest first. Dated in the
    /// past because a reading cannot be dated ahead of the clock, and after the demo meters were
    /// fitted because a reading cannot precede its own installation.
    /// </summary>
    private static IEnumerable<DateTimeOffset> CycleDates(DateTimeOffset now)
    {
        var thisMonth = new DateTimeOffset(now.Year, now.Month, 1, ReadHourUtc, 0, 0, TimeSpan.Zero);

        for (var cycle = Cycles; cycle >= 1; cycle--)
        {
            yield return thisMonth.AddMonths(-cycle);
        }
    }

    private static ReadingBaseline Baseline(Meter meter, Dictionary<Guid, List<MeterReading>> atPremise) =>
        meter.ServiceLocationId is { } premise && atPremise.TryGetValue(premise, out var readings)
            ? ReadingBaseline.From(meter, readings)
            : ReadingBaseline.From(meter, []);

    private static MeterReadingRequest Describe(Meter meter, ReadingBaseline baseline) =>
        new(
            meter.Id,
            meter.MeterNumber,
            meter.Type.ToString(),
            meter.RegisterDigits,
            baseline.Reading,
            baseline.ReadAt);
}
