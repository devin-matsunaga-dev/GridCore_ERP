using GridCore.Contracts.Providers;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;

namespace GridCore.Modules.Metering.Simulation;

/// <summary>
/// The MVP's meter reading provider: a simulated route book, standing in for the AMI head-end or
/// hand-held reader import a production deployment would configure in its place.
/// </summary>
/// <remarks>
/// <para>
/// This is the only thing in GridCore that invents a meter reading, and it lives behind
/// <see cref="IMeterReadingProvider"/> so nothing in the domain ever calls it by name
/// (ARCHITECTURE.md invariant 6). It produces dial readings and nothing else: it does not know what
/// consumption is, does not decide what a reading means, and never touches the register. The
/// Metering module works out consumption, rollover and the exception codes from what comes back —
/// which is exactly what it will have to do with a real provider that cannot be trusted to
/// classify its own output.
/// </para>
/// <para>
/// <b>Reproducibility is the requirement, not a nicety.</b> The same cycle read twice with the same
/// seed produces the same readings, the same missing reads and the same high-usage meters. Each
/// meter draws from its own stream keyed on the seed, the cycle and its meter number, so a meter's
/// reading depends on which meter it is and not on where it fell in the batch — which means fitting
/// a new meter somewhere does not move everybody else's numbers, and a test can assert on one meter
/// without pinning the whole route. The number rather than the id, so that a seeded demo world is
/// the same world on every machine.
/// </para>
/// <para>
/// The exceptions SPEC.md asks for fall out of that: a proportion of meters come back unread, a
/// proportion come back with the dials where they were, and a proportion come back far too high.
/// Rollover is not among them — it is not an exception but the ordinary end of a register's life,
/// and it happens here for the real reason, because a meter near the top of its register was given
/// more units than it had left.
/// </para>
/// </remarks>
public sealed class SimulatedMeterReadingProvider : IMeterReadingProvider
{
    /// <summary>How often a meter comes back unread — a locked gate, a dog, a dead comms module.</summary>
    public const decimal MissingReadChance = 0.04m;

    /// <summary>How often the dials have not moved at all — an empty property, or a stopped meter.</summary>
    public const decimal ZeroUsageChance = 0.06m;

    /// <summary>How often a premise comes back using far more than it should — a leak, or a new load.</summary>
    public const decimal HighUsageChance = 0.05m;

    /// <summary>Days a period is assumed to cover when a meter has never been read before.</summary>
    public const int AssumedCycleDays = 30;

    /// <summary>How much of its ordinary usage a high-usage premise comes back with, at least.</summary>
    private const decimal HighUsageFloor = 5m;

    /// <summary>And at most.</summary>
    private const decimal HighUsageCeiling = 9m;

    /// <inheritdoc />
    public string Name => "Simulated meter reading provider";

    /// <inheritdoc />
    public Task<MeterReadingBatch> ReadCycleAsync(MeterReadingCycle cycle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        var readings = cycle.Meters
            .Select(meter => Read(meter, cycle.Seed, cycle.CycleCode, cycle.ReadAt))
            .ToList();

        return Task.FromResult(new MeterReadingBatch(cycle.CycleCode, cycle.ReadAt, cycle.Seed, readings));
    }

    /// <summary>
    /// What a premise on this kind of meter uses in an ordinary day, before jitter. Not a tariff and
    /// not a rate — the shape of a load, so a demo's numbers look like a utility's rather than like
    /// a random number generator's.
    /// </summary>
    private static decimal TypicalDailyUnits(string meterType) =>
        Enum.TryParse<MeterType>(meterType, out var parsed)
            ? parsed switch
            {
                MeterType.SinglePhase => 18m,
                MeterType.ThreePhase => 120m,
                MeterType.CurrentTransformer => 900m,
                MeterType.Demand => 1_400m,
                _ => 18m,
            }
            // A type this provider does not recognise still gets read: a provider that refused
            // unknown devices would fail a whole route over one new meter class.
            : 18m;

    private static MeterReadingResult Read(MeterReadingRequest meter, int seed, string cycleCode, DateTimeOffset readAt)
    {
        // Keyed on the meter NUMBER, not the id — see DeterministicRandom.For. Ids carry random
        // bits, so keying on one would give every freshly seeded database a different demo world.
        var stream = DeterministicRandom.For(seed, cycleCode, meter.MeterNumber);

        // Drawn first and always, so the outcome a meter gets does not depend on how many values the
        // branches before it happened to consume.
        var outcome = stream.NextUnit();

        if (outcome < MissingReadChance)
        {
            return new MeterReadingResult(meter.MeterId, Reading: null, readAt, "No access to the meter");
        }

        var days = meter.LastReadAt is { } last
            ? ReadingAssessment.DaysBetween(last, readAt)
            : AssumedCycleDays;

        // A meter never read before starts wherever it was fitted; a meter never read and never
        // recorded starts at zero, which is what a new register reads.
        var previous = meter.LastReading ?? 0m;

        if (outcome < MissingReadChance + ZeroUsageChance)
        {
            return new MeterReadingResult(meter.MeterId, previous, readAt, "Dials unchanged since the last read");
        }

        // ±25% around the load's ordinary shape, so consecutive cycles differ the way weather does.
        var daily = TypicalDailyUnits(meter.MeterType) * stream.NextDecimal(0.75m, 1.25m);

        string? note = null;

        if (outcome < MissingReadChance + ZeroUsageChance + HighUsageChance)
        {
            daily *= stream.NextDecimal(HighUsageFloor, HighUsageCeiling);
            note = "Consumption well above the premise's usual";
        }

        var used = decimal.Round(daily * days, MeterReading.DecimalPlaces);

        return new MeterReadingResult(meter.MeterId, Advance(previous, used, meter.RegisterDigits), readAt, note);
    }

    /// <summary>
    /// Winds the dials on by <paramref name="used"/>, rolling the register over where it fills.
    /// </summary>
    /// <remarks>
    /// A real meter cannot display a number wider than its register, so neither may this. The
    /// rollover is not simulated for its own sake — it happens to whichever meter was near the top
    /// of its register, which is the only way it happens in the field, and it is what proves the
    /// module's rollover arithmetic against something that did not know it was being tested.
    /// </remarks>
    private static decimal Advance(decimal previous, decimal used, int registerDigits)
    {
        var capacity = ConsumptionCalculator.CapacityOf(registerDigits);

        // A previous reading the register cannot display is another provider's problem, not this
        // one's — clamped rather than thrown, so one bad row cannot fail a whole route.
        var dials = (previous % capacity) + used;

        return decimal.Round(dials % capacity, MeterReading.DecimalPlaces);
    }
}
