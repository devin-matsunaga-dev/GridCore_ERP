using GridCore.Modules.Metering.Data;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Metering.Seeding;
using GridCore.Modules.Metering.Simulation;
using GridCore.Modules.Metering.UnitTests.Infrastructure;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Metering.UnitTests.Seeding;

/// <summary>
/// The demo world's reading history. Seeded through the real provider and the real aggregate, so
/// these assertions are also a check that the demo world is one the domain rules actually permit —
/// an impossible reading fails here rather than shipping a figure nothing explains.
/// </summary>
public sealed class MeterReadingsDemoSeederTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    private readonly MeteringTestHost _host = new(new FakeClock(Now));

    public void Dispose() => _host.Dispose();

    private static FakeServiceLocationDirectory SeededPremises()
    {
        var directory = new FakeServiceLocationDirectory();

        for (var ordinal = 1; ordinal <= 10; ordinal++)
        {
            directory.Add($"L-{ordinal:D6}");
        }

        return directory;
    }

    private async Task<List<MeterReading>> SeededAsync()
    {
        await using (var write = _host.NewMeteringContext())
        {
            // Both seeders, in the order the runner runs them and each saving as its own unit of
            // work would — which is what lets the second one see the first one's meters at all.
            await new MetersDemoSeeder(write, SeededPremises(), new FakeClock(Now)).SeedAsync(CancellationToken.None);
            await write.SaveChangesAsync();
        }

        await using (var write = _host.NewMeteringContext())
        {
            await new MeterReadingsDemoSeeder(write, new SimulatedMeterReadingProvider(), new FakeClock(Now))
                .SeedAsync(CancellationToken.None);

            await write.SaveChangesAsync();
        }

        await using var read = _host.NewMeteringContext();

        return await read.MeterReadings.OrderBy(reading => reading.Id).ToListAsync();
    }

    [Fact]
    public void The_seeder_is_named_and_ordered_after_the_meters_it_reads()
    {
        IDemoSeeder seeder = new MeterReadingsDemoSeeder(null!, null!, TimeProvider.System);

        // The name is the dedupe key and is never renamed — a rename seeds a second year of readings.
        Assert.Equal("metering.readings", seeder.Name);
        Assert.Equal(700, seeder.Order);
    }

    [Fact]
    public async Task Every_fitted_meter_is_read_in_every_cycle()
    {
        var readings = await SeededAsync();

        await using var read = _host.NewMeteringContext();

        var fitted = await read.Meters.CountAsync(meter => meter.ServiceLocationId != null);

        Assert.Equal(fitted * MeterReadingsDemoSeeder.Cycles, readings.Count);
        Assert.Equal(MeterReadingsDemoSeeder.Cycles, readings.Select(reading => reading.CycleCode).Distinct().Count());
    }

    [Fact]
    public async Task A_meter_in_a_store_is_never_read()
    {
        await SeededAsync();

        await using var read = _host.NewMeteringContext();

        var unfitted = await read.Meters
            .Where(meter => meter.ServiceLocationId == null)
            .Select(meter => meter.Id)
            .ToListAsync();

        Assert.NotEmpty(unfitted);
        Assert.Empty(await read.MeterReadings.Where(reading => unfitted.Contains(reading.MeterId)).ToListAsync());
    }

    [Fact]
    public async Task Every_reading_is_dated_in_the_past_and_after_its_meter_was_fitted()
    {
        // Both are guards in the aggregate, so a violation would throw rather than fail here — this
        // pins the seeder's own arithmetic, which is what has to keep a year of cycles inside the
        // window between the demo installation and today.
        var readings = await SeededAsync();

        await using var read = _host.NewMeteringContext();

        var fittedAt = await read.Meters
            .Where(meter => meter.InstalledAt != null)
            .ToDictionaryAsync(meter => meter.Id, meter => meter.InstalledAt!.Value);

        Assert.All(readings, reading =>
        {
            Assert.True(reading.ReadingDate < Now);
            Assert.True(reading.ReadingDate >= fittedAt[reading.MeterId]);
        });
    }

    [Fact]
    public async Task Consumption_is_explained_by_the_pair_of_readings_on_the_line()
    {
        // Every figure the demo shows is arithmetic somebody can redo. The stamped previous reading
        // and the stamped consumption have to agree, on every line, or the register is telling a
        // story its own numbers do not support.
        var readings = await SeededAsync();

        await using var read = _host.NewMeteringContext();

        var registers = await read.Meters.ToDictionaryAsync(meter => meter.Id, meter => meter.RegisterDigits);

        Assert.All(
            readings.Where(reading => reading.Consumption is not null),
            reading => Assert.Equal(
                ConsumptionCalculator.Between(reading.PreviousReading!.Value, reading.Reading!.Value, registers[reading.MeterId]),
                new ConsumptionResult(reading.Consumption!.Value, reading.RolledOver)));
    }

    [Fact]
    public async Task The_readings_of_one_meter_run_forward_in_time()
    {
        var readings = await SeededAsync();

        foreach (var meter in readings.GroupBy(reading => reading.MeterId))
        {
            var dates = meter.OrderBy(reading => reading.Id).Select(reading => reading.ReadingDate).ToList();

            Assert.Equal(dates.OrderBy(date => date), dates);
        }
    }

    [Fact]
    public async Task The_demo_world_opens_with_a_worklist_worth_looking_at()
    {
        // The awkward states a screen has to render, exactly as WP-1.4's stock seeder seeds a shelf
        // below its reorder level. A demo whose exception queue is empty proves nothing about the
        // screen that shows it.
        var readings = await SeededAsync();

        Assert.Contains(readings, reading => reading.ExceptionCode is ReadingExceptionCode.MissingRead);
        Assert.Contains(readings, reading => reading.ExceptionCode is ReadingExceptionCode.ZeroUsage);
        Assert.Contains(readings, reading => reading.ExceptionCode is ReadingExceptionCode.HighUsage);
    }

    [Fact]
    public async Task The_same_seed_seeds_the_same_demo_world_twice()
    {
        // The point of the provider taking a seed at all: every developer's machine and CI see the
        // same meters unread and the same premise using far too much.
        var first = await SeededAsync();

        using var second = new MeteringTestHost(new FakeClock(Now));

        await using (var write = second.NewMeteringContext())
        {
            await new MetersDemoSeeder(write, SeededPremises(), new FakeClock(Now)).SeedAsync(CancellationToken.None);
            await write.SaveChangesAsync();
        }

        await using (var write = second.NewMeteringContext())
        {
            await new MeterReadingsDemoSeeder(write, new SimulatedMeterReadingProvider(), new FakeClock(Now))
                .SeedAsync(CancellationToken.None);

            await write.SaveChangesAsync();
        }

        await using var read = second.NewMeteringContext();

        Assert.Equal(
            Fingerprint(first),
            Fingerprint(await read.MeterReadings.OrderBy(reading => reading.Id).ToListAsync()));
    }

    [Fact]
    public async Task Nothing_is_read_where_nothing_is_fitted()
    {
        // A database with no customers has no premises, so it has no fitted meters either. Not an
        // error: a never-seeded database is fully functional (invariant 8).
        await using var write = _host.NewMeteringContext();

        await new MeterReadingsDemoSeeder(write, new SimulatedMeterReadingProvider(), new FakeClock(Now))
            .SeedAsync(CancellationToken.None);

        await write.SaveChangesAsync();

        Assert.Empty(await write.MeterReadings.ToListAsync());
    }

    private static string[] Fingerprint(IEnumerable<MeterReading> readings) =>
        // Keyed on the meter number rather than the id: ids are Guid v7 and carry random bits, so
        // two seeded worlds are the same world without being the same rows.
        [.. readings.Select(reading =>
            $"{reading.CycleCode}/{reading.Reading}/{reading.Consumption}/{reading.ExceptionCode}/{reading.RolledOver}")];
}
