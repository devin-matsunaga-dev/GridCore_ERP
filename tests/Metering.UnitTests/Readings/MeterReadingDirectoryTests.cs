using GridCore.Modules.Metering.Features.Meters;
using GridCore.Contracts.Directories;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Metering.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Metering.UnitTests.Readings;

/// <summary>
/// The reading register as the rest of GridCore reads it — the seam Billing (WP-2.3) raises every
/// bill from, and the reason it never learns a <c>metering</c> schema exists.
/// </summary>
public class MeterReadingDirectoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    private static MeteringTestHost NewHost(ScriptedMeterReadingProvider? provider = null) =>
        NewHost(new FakeClock(Now), provider);

    private static MeteringTestHost NewHost(FakeClock clock, ScriptedMeterReadingProvider? provider = null) =>
        new(clock, new FakeCurrentUser("auth0|reader", "A meter reader"), provider);

    private static Task<TResult> WithDirectoryAsync<TResult>(
        MeteringTestHost host,
        Func<IMeterReadingDirectory, Task<TResult>> work) =>
        host.InScopeAsync(services => work(services.GetRequiredService<IMeterReadingDirectory>()));

    [Fact]
    public async Task A_reading_crosses_the_boundary_with_the_number_of_the_meter_that_produced_it()
    {
        // Joined here rather than left to the caller: the meter number is printed on every bill
        // raised from a reading, and a caller holding only the id would need a meter directory too.
        using var host = NewHost();

        var (meter, _) = await host.FitMeterAsync("SN-DIR-1");

        var reading = await host.WithReadingsAsync(readings =>
            readings.RecordAsync(meter.Meter.Id, new RecordReadingInput(1_250m)));

        var summary = await WithDirectoryAsync(host, directory => directory.FindAsync(reading.Id));

        Assert.NotNull(summary);
        Assert.Equal(meter.Meter.MeterNumber, summary.MeterNumber);
        Assert.Equal(meter.Meter.Id, summary.MeterId);
        Assert.Equal(1_250m, summary.Reading);
    }

    [Fact]
    public async Task Everything_a_bill_is_raised_from_is_on_the_summary()
    {
        // Consumption, the dials either side of it and the period are all stamped on the reading
        // (WP-2.2), so a bill can reproduce itself years later without re-deriving anything.
        using var host = NewHost();

        var (meter, _) = await host.FitMeterAsync("SN-DIR-2", installationReading: 1_000m);

        await host.WithReadingsAsync(readings => readings.RecordAsync(meter.Meter.Id, new RecordReadingInput(1_600m)));

        var summary = Assert.Single(await WithDirectoryAsync(host, directory =>
            directory.AtLocationAsync(meter.Meter.ServiceLocationId!.Value, 10)));

        Assert.Equal(1_000m, summary.PreviousReading);
        Assert.Equal(1_600m, summary.Reading);
        Assert.Equal(600m, summary.Consumption);
        Assert.NotNull(summary.PreviousReadingDate);
    }

    [Fact]
    public async Task An_exception_code_crosses_the_boundary_by_name_not_as_an_enum()
    {
        // Contracts takes no dependency on this module's types. Billing gates on this string: a
        // flagged reading is worked by hand before it becomes a bill.
        using var host = NewHost();

        var (meter, _) = await host.FitMeterAsync("SN-DIR-3", installationReading: 1_000m);

        // The dials have not moved, which is ZeroUsage.
        await host.WithReadingsAsync(readings => readings.RecordAsync(meter.Meter.Id, new RecordReadingInput(1_000m)));

        var summary = Assert.Single(await WithDirectoryAsync(host, directory =>
            directory.AtLocationAsync(meter.Meter.ServiceLocationId!.Value, 10)));

        Assert.Equal(nameof(ReadingExceptionCode.ZeroUsage), summary.ExceptionCode);
        Assert.True(summary.IsException);
    }

    [Fact]
    public async Task An_ordinary_reading_is_not_on_the_worklist()
    {
        using var host = NewHost();

        var (meter, _) = await host.FitMeterAsync("SN-DIR-4", installationReading: 1_000m);

        await host.WithReadingsAsync(readings => readings.RecordAsync(meter.Meter.Id, new RecordReadingInput(1_400m)));

        var summary = Assert.Single(await WithDirectoryAsync(host, directory =>
            directory.AtLocationAsync(meter.Meter.ServiceLocationId!.Value, 10)));

        Assert.Equal(nameof(ReadingExceptionCode.None), summary.ExceptionCode);
        Assert.False(summary.IsException);
    }

    [Fact]
    public async Task A_cycle_is_handed_over_oldest_first()
    {
        // Unlike every other list in GridCore. A billing run walks a cycle in the order it was read,
        // and a caller that has to reverse a page before using it is one refactor away from billing
        // a cycle backwards.
        using var host = NewHost();

        await host.FitMeterAsync("SN-DIR-5", locationCode: "L-000001");
        await host.FitMeterAsync("SN-DIR-6", locationCode: "L-000002");

        await host.WithReadingsAsync(readings => readings.RunCycleAsync(new RunReadingCycleInput("2026-08", Seed: 7)));

        var cycle = await WithDirectoryAsync(host, directory => directory.ForCycleAsync("2026-08", 100));

        Assert.Equal(2, cycle.Count);
        Assert.Equal(cycle.Select(reading => reading.Id).OrderBy(id => id), cycle.Select(reading => reading.Id));
        Assert.All(cycle, reading => Assert.Equal("2026-08", reading.CycleCode));
    }

    [Fact]
    public async Task A_missing_read_reaches_the_caller_as_a_row_with_no_reading()
    {
        // A cycle that could not read a meter is a real outcome and billing has to know it: dropping
        // it would leave the utility unable to tell "used nothing" from "nobody went" (WP-2.2).
        var provider = new ScriptedMeterReadingProvider();

        using var host = NewHost(provider);

        var (meter, _) = await host.FitMeterAsync("SN-DIR-7");

        provider.Returns(meter.Meter.Id, null);

        await host.WithReadingsAsync(readings => readings.RunCycleAsync(new RunReadingCycleInput("2026-08")));

        var summary = Assert.Single(await WithDirectoryAsync(host, directory => directory.ForCycleAsync("2026-08", 100)));

        Assert.Null(summary.Reading);
        Assert.Null(summary.Consumption);
        Assert.Equal(nameof(ReadingExceptionCode.MissingRead), summary.ExceptionCode);
    }

    [Fact]
    public async Task A_manual_reading_carries_no_cycle_code_and_is_not_in_any_cycle()
    {
        using var host = NewHost();

        var (meter, _) = await host.FitMeterAsync("SN-DIR-8");

        await host.WithReadingsAsync(readings => readings.RecordAsync(meter.Meter.Id, new RecordReadingInput(1_100m)));

        Assert.Empty(await WithDirectoryAsync(host, directory => directory.ForCycleAsync("2026-08", 100)));

        var summary = Assert.Single(await WithDirectoryAsync(host, directory =>
            directory.AtLocationAsync(meter.Meter.ServiceLocationId!.Value, 10)));

        Assert.Null(summary.CycleCode);
    }

    [Fact]
    public async Task A_premise_is_read_across_every_meter_that_has_stood_there()
    {
        // The question belongs to the place, not the device — which is why WP-2.2 stamps the premise
        // on every reading rather than reading it off the meter.
        //
        // The clock is advanced between the two readings, and has to be: ids are Guid v7 stamped
        // from the instant they are created, so two rows minted in the same frozen millisecond have
        // no defined order and a register ordered by key would return them either way round.
        var clock = new FakeClock(Now);

        using var host = NewHost(clock);

        var (first, premise) = await host.FitMeterAsync("SN-DIR-9", installationReading: 1_000m);

        await host.WithReadingsAsync(readings => readings.RecordAsync(first.Meter.Id, new RecordReadingInput(1_400m)));

        clock.Advance(TimeSpan.FromDays(30));

        await host.WithMetersAsync(meters =>
            meters.RemoveAsync(first.Meter.Id, "Exchanged."));

        var replacement = await host.RegisterMeterAsync("SN-DIR-10");

        await host.WithMetersAsync(meters =>
            meters.AssignAsync(replacement.Meter.Id, new AssignMeterInput(premise, 0m)));

        clock.Advance(TimeSpan.FromDays(30));

        await host.WithReadingsAsync(readings => readings.RecordAsync(replacement.Meter.Id, new RecordReadingInput(200m)));

        var atPremise = await WithDirectoryAsync(host, directory => directory.AtLocationAsync(premise, 10));

        Assert.Equal(2, atPremise.Count);
        Assert.Equal(
            [replacement.Meter.MeterNumber, first.Meter.MeterNumber],
            atPremise.Select(reading => reading.MeterNumber));
    }

    [Fact]
    public async Task An_id_that_matches_nothing_is_null_rather_than_a_throw()
    {
        using var host = NewHost();

        Assert.Null(await WithDirectoryAsync(host, directory => directory.FindAsync(Guid.CreateVersion7())));
    }

    [Fact]
    public async Task An_unread_cycle_is_an_empty_list_rather_than_a_failure() =>
        // A billing run over a cycle nobody has read yet answers "nothing to bill", not an error.
        Assert.Empty(await WithDirectoryAsync(NewHost(), directory => directory.ForCycleAsync("2099-01", 100)));

    [Fact]
    public async Task A_cycle_code_that_is_missing_is_refused_rather_than_matching_everything()
    {
        // A blank code would otherwise translate to "cycle_code = ''", which matches nothing on
        // Postgres and every manual reading's NULL on nobody's reading of the intent.
        using var host = NewHost();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            WithDirectoryAsync(host, directory => directory.ForCycleAsync("   ", 100)));
    }
}
