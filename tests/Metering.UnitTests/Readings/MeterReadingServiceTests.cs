using GridCore.Contracts.Events;
using GridCore.Contracts.Providers;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Modules.Metering.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Metering.UnitTests.Readings;

/// <summary>
/// The reading register over the real EF model on SQLite in-memory. What these assert that the
/// aggregate tests cannot: that the baseline is assembled out of the database correctly, and that
/// the reading, its audit entry and its outbox row commit in one transaction.
/// </summary>
public sealed class MeterReadingServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeClock _clock = new(Now);
    private readonly MeteringTestHost _host;

    public MeterReadingServiceTests() =>
        _host = new MeteringTestHost(_clock, new FakeCurrentUser("technician-1", "Jesse Atalig"));

    public void Dispose() => _host.Dispose();

    private Task<MeterReading> RecordAsync(Guid meterId, decimal? reading, DateTimeOffset? on = null, string? note = null) =>
        _host.WithReadingsAsync(readings => readings.RecordAsync(meterId, new RecordReadingInput(reading, on, note)));

    [Fact]
    public async Task A_reading_is_measured_from_what_the_meter_read_when_it_was_fitted()
    {
        var (meter, premise) = await _host.FitMeterAsync("SEN-1", installationReading: 14_820.500m);

        var reading = await RecordAsync(meter.Meter.Id, 15_120.750m);

        Assert.Equal(300.250m, reading.Consumption);
        Assert.Equal(14_820.500m, reading.PreviousReading);
        Assert.Equal(premise, reading.ServiceLocationId);
        Assert.Equal(MeterReadingSource.Manual, reading.Source);
    }

    [Fact]
    public async Task The_second_reading_is_measured_from_the_first()
    {
        var (meter, _) = await _host.FitMeterAsync("SEN-1");

        _clock.Advance(TimeSpan.FromDays(30));
        await RecordAsync(meter.Meter.Id, 600m);

        _clock.Advance(TimeSpan.FromDays(30));
        var second = await RecordAsync(meter.Meter.Id, 900m);

        Assert.Equal(300m, second.Consumption);
        Assert.Equal(600m, second.PreviousReading);
    }

    [Fact]
    public async Task A_reading_its_audit_entry_and_its_event_commit_together()
    {
        var (meter, _) = await _host.FitMeterAsync("SEN-1");

        var reading = await RecordAsync(meter.Meter.Id, 540m, note: "Read off the card");

        await using var metering = _host.NewMeteringContext();
        await using var platform = _host.NewPlatformContext();

        var stored = await metering.MeterReadings.SingleAsync();
        var audited = await platform.AuditEntries.SingleAsync(entry => entry.Action == AuditActions.MeterReadingRecorded);

        Assert.Equal(reading.Id, stored.Id);
        Assert.Equal(AuditEntityTypes.MeterReading, audited.EntityType);
        Assert.Equal(reading.Id.ToString(), audited.EntityId);
        Assert.Null(audited.BeforeJson);
        Assert.Contains("MTR-000001", audited.AfterJson, StringComparison.Ordinal);

        var published = _host.Events.Single<MeterReadingRecorded>();

        Assert.Equal(reading.Id, published.ReadingId);
        Assert.Equal("MTR-000001", published.MeterNumber);
        Assert.Equal(stored.ServiceLocationId, published.ServiceLocationId);
        Assert.Equal("None", published.ExceptionCode);
        Assert.Null(published.CycleCode);
    }

    [Fact]
    public async Task A_missing_read_is_published_too_with_no_reading_on_it()
    {
        // A cycle that could not read a meter is a fact billing has to know: the alternative is a
        // bill quietly raised on stale dials.
        var (meter, _) = await _host.FitMeterAsync("SEN-1");

        await RecordAsync(meter.Meter.Id, null, note: "Gate padlocked");

        var published = _host.Events.Single<MeterReadingRecorded>();

        Assert.Null(published.Reading);
        Assert.Null(published.Consumption);
        Assert.Equal("MissingRead", published.ExceptionCode);
    }

    [Fact]
    public async Task A_missing_read_does_not_become_the_next_readings_baseline()
    {
        // The exact previous reading is fetched as itself rather than hoped for inside a window, so
        // an unread month does not quietly report the next period's consumption as nothing.
        var (meter, _) = await _host.FitMeterAsync("SEN-1");

        _clock.Advance(TimeSpan.FromDays(30));
        await RecordAsync(meter.Meter.Id, 600m);

        _clock.Advance(TimeSpan.FromDays(30));
        await RecordAsync(meter.Meter.Id, null);

        _clock.Advance(TimeSpan.FromDays(30));
        var after = await RecordAsync(meter.Meter.Id, 1_200m);

        Assert.Equal(600m, after.PreviousReading);
        Assert.Equal(600m, after.Consumption);
    }

    [Fact]
    public async Task An_exchange_measures_from_the_new_meters_own_dials_but_keeps_the_premises_usage_profile()
    {
        // The two halves of a baseline pulling in different directions, over real rows. Dials belong
        // to the device; what a house uses belongs to the house.
        var (outgoing, premise) = await _host.FitMeterAsync("SEN-1");

        _clock.Advance(TimeSpan.FromDays(30));
        await RecordAsync(outgoing.Meter.Id, 600m);

        _clock.Advance(TimeSpan.FromDays(1));
        await _host.WithMetersAsync(meters => meters.RemoveAsync(outgoing.Meter.Id, "Exchanged"));

        var incoming = await _host.RegisterMeterAsync("SEN-2");
        await _host.WithMetersAsync(meters => meters.AssignAsync(incoming.Meter.Id, new AssignMeterInput(premise, 5m)));

        _clock.Advance(TimeSpan.FromDays(30));
        var reading = await RecordAsync(incoming.Meter.Id, 25m);

        // Measured from the exchange meter's own installation reading, not from 600.
        Assert.Equal(5m, reading.PreviousReading);
        Assert.Equal(20m, reading.Consumption);

        // But the premise's twenty-a-day history is still what a high-usage check would judge by,
        // which is why this reading is not flagged and a wild one would be.
        _clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(ReadingExceptionCode.HighUsage, (await RecordAsync(incoming.Meter.Id, 5_000m)).ExceptionCode);
    }

    [Fact]
    public async Task A_meter_that_is_not_fitted_cannot_be_read()
    {
        // Failure path: a meter in a store measures nothing.
        var meter = await _host.RegisterMeterAsync("SEN-1");

        await Assert.ThrowsAsync<MeterWorkflowException>(() => RecordAsync(meter.Meter.Id, 540m));
    }

    [Fact]
    public async Task A_meter_that_does_not_exist_is_a_not_found() =>
        await Assert.ThrowsAsync<MeterNotFoundException>(() => RecordAsync(Guid.CreateVersion7(), 540m));

    [Fact]
    public async Task Nothing_is_written_when_a_reading_is_refused()
    {
        // The transaction rolls back, so a refused reading leaves no row, no audit entry and no
        // event — the three of them are one write or none.
        var meter = await _host.RegisterMeterAsync("SEN-1");

        await Assert.ThrowsAsync<MeterWorkflowException>(() => RecordAsync(meter.Meter.Id, 540m));

        await using var metering = _host.NewMeteringContext();
        await using var platform = _host.NewPlatformContext();

        Assert.Empty(await metering.MeterReadings.ToListAsync());
        Assert.Empty(await platform.AuditEntries.Where(entry => entry.Action == AuditActions.MeterReadingRecorded).ToListAsync());
        Assert.Empty(_host.Events.Published.OfType<MeterReadingRecorded>());
    }

    [Fact]
    public async Task A_cycle_reads_every_fitted_meter_and_leaves_the_ones_in_a_store_alone()
    {
        var (first, _) = await _host.FitMeterAsync("SEN-1", locationCode: "L-000001");
        var (second, _) = await _host.FitMeterAsync("SEN-2", locationCode: "L-000002");
        var inStore = await _host.RegisterMeterAsync("SEN-3");

        _clock.Advance(TimeSpan.FromDays(30));

        var cycle = await _host.WithReadingsAsync(readings => readings.RunCycleAsync(new RunReadingCycleInput("2026-08", Seed: 4471)));

        Assert.Equal(2, cycle.Recorded);
        Assert.Contains(cycle.Readings, reading => reading.MeterId == first.Meter.Id);
        Assert.Contains(cycle.Readings, reading => reading.MeterId == second.Meter.Id);
        Assert.DoesNotContain(cycle.Readings, reading => reading.MeterId == inStore.Meter.Id);
        Assert.All(cycle.Readings, reading => Assert.Equal("2026-08", reading.CycleCode));
        Assert.All(cycle.Readings, reading => Assert.Equal(MeterReadingSource.Cycle, reading.Source));
    }

    [Fact]
    public async Task A_cycle_leaves_one_audit_entry_naming_the_run_and_one_event_per_reading()
    {
        // Invariant 1 is about the write endpoint, and a cycle is one act — but each reading is its
        // own fact, so the events are per reading.
        await _host.FitMeterAsync("SEN-1", locationCode: "L-000001");
        await _host.FitMeterAsync("SEN-2", locationCode: "L-000002");

        _clock.Advance(TimeSpan.FromDays(30));

        var cycle = await _host.WithReadingsAsync(readings => readings.RunCycleAsync(new RunReadingCycleInput("2026-08", Seed: 4471)));

        await using var platform = _host.NewPlatformContext();

        var audited = await platform.AuditEntries.SingleAsync(entry => entry.Action == AuditActions.MeterReadingCycleRun);

        Assert.Equal(AuditEntityTypes.MeterReadingCycle, audited.EntityType);
        Assert.Equal("2026-08", audited.EntityId);
        Assert.Contains(cycle.Provider, audited.AfterJson, StringComparison.Ordinal);

        Assert.Equal(cycle.Recorded, _host.Events.Published.OfType<MeterReadingRecorded>().Count());
    }

    [Fact]
    public async Task The_same_cycle_cannot_be_read_twice()
    {
        // Failure path, and the reason for the unique index behind it: a second press of the button
        // would otherwise double every consumption figure the cycle produced.
        await _host.FitMeterAsync("SEN-1");

        _clock.Advance(TimeSpan.FromDays(30));
        await _host.WithReadingsAsync(readings => readings.RunCycleAsync(new RunReadingCycleInput("2026-08")));

        _clock.Advance(TimeSpan.FromDays(1));

        var refused = await Assert.ThrowsAsync<MeterWorkflowException>(() =>
            _host.WithReadingsAsync(readings => readings.RunCycleAsync(new RunReadingCycleInput("2026-08"))));

        Assert.Contains("2026-08", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cycle_with_no_cycle_code_is_refused() =>
        await Assert.ThrowsAsync<MeterValidationException>(() =>
            _host.WithReadingsAsync(readings => readings.RunCycleAsync(new RunReadingCycleInput("   "))));

    [Fact]
    public async Task A_result_for_a_meter_that_was_not_on_the_route_is_ignored()
    {
        // A provider that answers for a meter nobody asked about is not trusted: it has no baseline
        // assembled, so recording it would attach a reading to arithmetic that never ran.
        var scripted = new ScriptedMeterReadingProvider();

        using var host = new MeteringTestHost(_clock, new FakeCurrentUser("technician-1"), scripted);

        var meter = await host.RegisterMeterAsync("SEN-1");
        var premise = host.ServiceLocations.Add("L-000001");

        await host.WithMetersAsync(meters => meters.AssignAsync(meter.Meter.Id, new AssignMeterInput(premise, 0m)));

        scripted.Returns(meter.Meter.Id, 540m);
        scripted.Extra.Add(new MeterReadingResult(Guid.CreateVersion7(), 99m, Now, null));

        var cycle = await host.WithReadingsAsync(readings => readings.RunCycleAsync(new RunReadingCycleInput("2026-08")));

        Assert.Equal(1, cycle.Recorded);
        Assert.Equal(meter.Meter.Id, cycle.Readings[0].MeterId);
    }

    [Fact]
    public async Task The_route_tells_the_provider_the_register_width_and_the_last_reading()
    {
        // What the provider is handed, asserted: without the register width it cannot produce a
        // reading that has wrapped rather than one the meter could not display.
        var scripted = new ScriptedMeterReadingProvider();

        using var host = new MeteringTestHost(_clock, new FakeCurrentUser("technician-1"), scripted);

        var meter = await host.RegisterMeterAsync("SEN-1", MeterType.ThreePhase, registerDigits: 6);
        var premise = host.ServiceLocations.Add("L-000001");

        await host.WithMetersAsync(meters => meters.AssignAsync(meter.Meter.Id, new AssignMeterInput(premise, 61_204m)));

        scripted.Returns(meter.Meter.Id, 61_500m);

        await host.WithReadingsAsync(readings => readings.RunCycleAsync(new RunReadingCycleInput("2026-08")));

        var described = Assert.Single(scripted.LastRoute);

        Assert.Equal("MTR-000001", described.MeterNumber);
        Assert.Equal("ThreePhase", described.MeterType);
        Assert.Equal(6, described.RegisterDigits);
        Assert.Equal(61_204m, described.LastReading);
    }

    [Fact]
    public async Task The_register_lists_a_meters_readings_newest_first()
    {
        var (meter, _) = await _host.FitMeterAsync("SEN-1");

        _clock.Advance(TimeSpan.FromDays(30));
        await RecordAsync(meter.Meter.Id, 600m);

        _clock.Advance(TimeSpan.FromDays(30));
        await RecordAsync(meter.Meter.Id, 900m);

        var listed = await _host.WithReadingsAsync(readings => readings.ForMeterAsync(meter.Meter.Id, 50));

        Assert.Equal([900m, 600m], listed.Select(reading => reading.Reading).ToArray());
    }

    [Fact]
    public async Task Asking_for_the_readings_of_a_meter_that_does_not_exist_is_a_not_found() =>
        // Distinguished from a meter that has simply never been read, which is an empty list.
        await Assert.ThrowsAsync<MeterNotFoundException>(() =>
            _host.WithReadingsAsync(readings => readings.ForMeterAsync(Guid.CreateVersion7(), 50)));

    [Fact]
    public async Task The_worklist_is_everything_that_is_not_an_ordinary_read()
    {
        var (first, _) = await _host.FitMeterAsync("SEN-1", locationCode: "L-000001");
        var (second, _) = await _host.FitMeterAsync("SEN-2", locationCode: "L-000002");

        _clock.Advance(TimeSpan.FromDays(30));

        await RecordAsync(first.Meter.Id, 600m);
        await RecordAsync(second.Meter.Id, null);

        _clock.Advance(TimeSpan.FromDays(30));
        await RecordAsync(first.Meter.Id, 600m);

        var worklist = await _host.WithReadingsAsync(readings => readings.ListAsync(new MeterReadingQuery(ExceptionsOnly: true)));

        Assert.Equal(
            [ReadingExceptionCode.ZeroUsage, ReadingExceptionCode.MissingRead],
            worklist.Select(reading => reading.ExceptionCode).ToArray());

        var missing = await _host.WithReadingsAsync(readings =>
            readings.ListAsync(new MeterReadingQuery(ExceptionCode: ReadingExceptionCode.MissingRead)));

        Assert.Equal(second.Meter.Id, Assert.Single(missing).MeterId);
    }

    [Fact]
    public async Task The_register_filters_by_premise_across_every_meter_that_has_stood_there()
    {
        var (outgoing, premise) = await _host.FitMeterAsync("SEN-1");

        _clock.Advance(TimeSpan.FromDays(30));
        await RecordAsync(outgoing.Meter.Id, 600m);

        await _host.WithMetersAsync(meters => meters.RemoveAsync(outgoing.Meter.Id, "Exchanged"));

        var incoming = await _host.RegisterMeterAsync("SEN-2");
        await _host.WithMetersAsync(meters => meters.AssignAsync(incoming.Meter.Id, new AssignMeterInput(premise, 0m)));

        _clock.Advance(TimeSpan.FromDays(30));
        await RecordAsync(incoming.Meter.Id, 400m);

        var atPremise = await _host.WithReadingsAsync(readings =>
            readings.ListAsync(new MeterReadingQuery(ServiceLocationId: premise)));

        Assert.Equal(2, atPremise.Count);
    }

    [Fact]
    public async Task A_page_is_clamped_to_the_registers_maximum()
    {
        var (meter, _) = await _host.FitMeterAsync("SEN-1");

        _clock.Advance(TimeSpan.FromDays(30));
        await RecordAsync(meter.Meter.Id, 600m);

        Assert.Single(await _host.WithReadingsAsync(readings =>
            readings.ListAsync(new MeterReadingQuery(Limit: MeterReadingService.MaxPageSize + 1_000))));
    }
}
