using GridCore.Contracts.Providers;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Metering.Simulation;
using GridCore.Platform.Simulation;

namespace GridCore.Modules.Metering.UnitTests.Simulation;

/// <summary>
/// The meter simulator. What matters about it is not that its numbers are pretty but that they are
/// <b>reproducible</b>: the same seed and cycle produce the same batch, including which meters come
/// back as exceptions. A demonstration whose figures move between runs cannot be reconciled, and a
/// test that cannot predict an exception cannot assert one.
/// </summary>
public sealed class SimulatedMeterReadingProviderTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastReadAt = new(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);

    private static Guid MeterId(int ordinal) => new($"0198f6a0-0000-7000-8000-{ordinal:D12}");

    private static MeterReadingCycle Cycle(
        int meters = 200,
        int seed = 4471,
        string cycleCode = "2026-08",
        MeterType type = MeterType.SinglePhase,
        int registerDigits = 5,
        decimal? lastReading = 1_000m) =>
        new(
            cycleCode,
            ReadAt,
            seed,
            [.. Enumerable.Range(1, meters).Select(ordinal =>
                new MeterReadingRequest(MeterId(ordinal), $"MTR-{ordinal:D6}", type.ToString(), registerDigits, lastReading, LastReadAt))]);

    private static async Task<IReadOnlyList<MeterReadingResult>> ReadAsync(MeterReadingCycle cycle) =>
        (await new SimulatedMeterReadingProvider().ReadCycleAsync(cycle)).Readings;

    [Fact]
    public async Task Every_meter_on_the_route_comes_back_with_a_result()
    {
        // Including the ones that could not be read. A batch that silently dropped them would leave
        // the utility unable to tell "used nothing" from "nobody went".
        var readings = await ReadAsync(Cycle(meters: 50));

        Assert.Equal(50, readings.Count);
        Assert.Equal(50, readings.Select(reading => reading.MeterId).Distinct().Count());
    }

    [Fact]
    public async Task The_same_seed_and_cycle_produce_exactly_the_same_batch()
    {
        // The work package's headline requirement.
        var first = await ReadAsync(Cycle());
        var second = await ReadAsync(Cycle());

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task A_different_seed_produces_a_different_batch() =>
        Assert.NotEqual(await ReadAsync(Cycle(seed: 4471)), await ReadAsync(Cycle(seed: 9999)));

    [Fact]
    public async Task The_same_seed_in_a_different_cycle_produces_a_different_batch() =>
        // Mixed in on purpose: a demo world where the same house is unread every single month reads
        // as a bug rather than as a simulation.
        Assert.NotEqual(await ReadAsync(Cycle(cycleCode: "2026-08")), await ReadAsync(Cycle(cycleCode: "2026-09")));

    [Fact]
    public async Task A_meters_reading_does_not_depend_on_who_else_was_on_the_route()
    {
        // Each meter draws from its own stream, keyed on the seed, the cycle and its id. So fitting a
        // meter somewhere does not move everybody else's numbers — and a test can pin one meter
        // without pinning the whole route.
        var whole = await ReadAsync(Cycle(meters: 200));
        var shorter = await ReadAsync(Cycle(meters: 5));

        Assert.Equal(
            whole.Where(reading => shorter.Any(other => other.MeterId == reading.MeterId)).ToList(),
            shorter);
    }

    [Fact]
    public async Task The_batch_reports_back_the_cycle_and_the_seed_it_was_produced_from()
    {
        // So the run can be reproduced from what it returned, without the caller having kept it.
        var batch = await new SimulatedMeterReadingProvider().ReadCycleAsync(Cycle(meters: 3));

        Assert.Equal("2026-08", batch.CycleCode);
        Assert.Equal(4471, batch.Seed);
        Assert.Equal(ReadAt, batch.ReadAt);
    }

    [Fact]
    public async Task All_three_kinds_of_exception_appear_over_a_realistic_route()
    {
        // SPEC.md asks for high-usage, zero-usage and missing-read exceptions. Asserted over 200
        // meters, where each is near-certain rather than merely likely — the exact proportions are
        // pinned by the case below, not by this one.
        var readings = await ReadAsync(Cycle(meters: 200));

        Assert.Contains(readings, reading => reading.Reading is null);
        Assert.Contains(readings, reading => reading.Reading == 1_000m);
        Assert.Contains(readings, reading => reading.Reading > 5_000m);
    }

    [Fact]
    public async Task The_exception_rates_are_roughly_what_the_provider_advertises()
    {
        // A drifting constant would quietly change every demo, so the advertised chances are held to
        // what actually comes out. Loose bounds: this is a sample of 200, not a proof about the
        // distribution.
        var readings = await ReadAsync(Cycle(meters: 200));

        var missing = readings.Count(reading => reading.Reading is null);
        var zero = readings.Count(reading => reading.Reading == 1_000m);

        Assert.InRange(missing, 1, 20);
        Assert.InRange(zero, 1, 25);
        Assert.True(missing + zero < readings.Count / 2);
    }

    [Fact]
    public async Task A_meter_that_could_not_be_read_says_so_rather_than_guessing()
    {
        var unread = Assert.Single((await ReadAsync(Cycle(meters: 200))).Where(reading => reading.Reading is null).Take(1));

        Assert.Null(unread.Reading);
        Assert.False(string.IsNullOrWhiteSpace(unread.Note));
    }

    [Fact]
    public async Task A_reading_never_exceeds_what_the_meters_register_can_display()
    {
        // The property that keeps the simulator honest: a real meter cannot show a number wider than
        // its register, and one that did would be refused by the module rather than recorded.
        foreach (var digits in new[] { 4, 5, 6 })
        {
            var readings = await ReadAsync(Cycle(meters: 100, registerDigits: digits, lastReading: null));

            Assert.All(readings, reading =>
                Assert.True(reading.Reading is null || ConsumptionCalculator.Fits(reading.Reading.Value, digits)));
        }
    }

    [Fact]
    public async Task A_meter_near_the_top_of_its_register_rolls_over_rather_than_reading_impossibly()
    {
        // Rollover is not simulated for its own sake — it happens to whichever meter had less left in
        // its register than it consumed, which is the only way it happens in the field.
        var readings = await ReadAsync(Cycle(meters: 100, registerDigits: 4, lastReading: 9_990m));

        Assert.Contains(readings, reading => reading.Reading < 9_990m && reading.Reading is not null);
        Assert.All(readings, reading => Assert.True(reading.Reading is null || reading.Reading < 10_000m));
    }

    [Fact]
    public async Task A_meter_that_has_never_been_read_starts_its_register_from_zero()
    {
        var readings = await ReadAsync(Cycle(meters: 20, lastReading: null));

        Assert.All(readings, reading => Assert.True(reading.Reading is null or >= 0m));
        Assert.Contains(readings, reading => reading.Reading is > 0m);
    }

    [Fact]
    public async Task A_bigger_service_reads_bigger_numbers()
    {
        // The load shape, not a tariff: a CT-metered intake consumes far more in a month than a
        // house, so a demo's figures look like a utility's rather than a generator's.
        var domestic = await ReadAsync(Cycle(meters: 40, type: MeterType.SinglePhase, registerDigits: 7, lastReading: 0m));
        var intake = await ReadAsync(Cycle(meters: 40, type: MeterType.CurrentTransformer, registerDigits: 7, lastReading: 0m));

        Assert.True(Total(intake) > Total(domestic) * 10m);
    }

    [Fact]
    public async Task A_meter_type_the_provider_does_not_recognise_is_still_read()
    {
        // A provider that refused an unknown device would fail a whole route over one new meter
        // class, which is exactly what a real head-end must not do.
        var readings = await ReadAsync(Cycle(meters: 5) with
        {
            Meters = [new MeterReadingRequest(MeterId(1), "MTR-000001", "SomethingNewIn2030", 5, 1_000m, LastReadAt)],
        });

        Assert.Single(readings);
    }

    [Fact]
    public async Task A_reading_is_never_finer_than_the_register_stores() =>
        // Otherwise the module would refuse the provider's own output — readings are stored to three
        // places and anything finer is refused rather than rounded.
        Assert.All(
            await ReadAsync(Cycle(meters: 100)),
            reading => Assert.True(
                reading.Reading is null
                || decimal.Round(reading.Reading.Value, MeterReading.DecimalPlaces) == reading.Reading.Value));

    [Fact]
    public void The_provider_names_itself_for_the_audit_trail() =>
        // A record of where numbers came from outlives whichever implementation was configured.
        Assert.False(string.IsNullOrWhiteSpace(new SimulatedMeterReadingProvider().Name));

    private static decimal Total(IEnumerable<MeterReadingResult> readings) =>
        readings.Sum(reading => reading.Reading ?? 0m);

    [Fact]
    public async Task A_meters_reading_does_not_depend_on_the_id_it_happens_to_have_been_given()
    {
        // The stream is keyed on the meter number, not the id. Ids carry random bits, so keying on
        // one would hand every freshly seeded database a different demo world — which is the one
        // thing a seed exists to prevent.
        var cycle = Cycle(meters: 30);

        var reissued = cycle with
        {
            Meters = [.. cycle.Meters.Select(meter => meter with { MeterId = Guid.CreateVersion7() })],
        };

        Assert.Equal(
            (await ReadAsync(cycle)).Select(reading => reading.Reading).ToArray(),
            (await ReadAsync(reissued)).Select(reading => reading.Reading).ToArray());
    }
}
