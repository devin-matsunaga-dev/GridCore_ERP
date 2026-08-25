using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Metering.Data;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GridCore.IntegrationTests;

/// <summary>
/// The reading register against real Postgres.
/// </summary>
/// <remarks>
/// The fast tier proves the arithmetic, the guards and the baseline rules with no infrastructure at
/// all — that is where nearly all of WP-2.2's cases live. What only a container can show is what
/// Postgres itself guarantees: that <c>numeric(18,3)</c> returns the exact figure a bill will be
/// raised from, that the one-reading-per-meter-per-cycle index really does refuse a second run, and
/// that a manual re-read is still allowed beside it because NULLs in a unique index are distinct.
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MeterReadingRegistryTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> AFittedMeterAsync(string serialNumber, string line1, decimal? installationReading)
    {
        Guid premise;

        await using (var scope = fixture.CreateScope())
        {
            premise = (await scope.ServiceProvider.GetRequiredService<IServiceLocationService>()
                .RegisterAsync(new ServiceLocationInput(
                    Address.Create(line1, "Songsong", "Rota", "MP", postalCode: "96951"),
                    "Meter on the north wall",
                    IsActive: true,
                    null)))
                .Id;
        }

        await using (var scope = fixture.CreateScope())
        {
            var meter = await scope.ServiceProvider.GetRequiredService<IMeterService>()
                .RegisterAsync(new RegisterMeterInput(serialNumber, MeterType.SinglePhase, Manufacturer: "Sensus"));

            await scope.ServiceProvider.GetRequiredService<IMeterService>()
                .AssignAsync(meter.Meter.Id, new AssignMeterInput(premise, installationReading));

            return meter.Meter.Id;
        }
    }

    [Fact]
    public async Task A_reading_and_its_consumption_survive_a_round_trip_through_numeric_18_3()
    {
        var meter = await AFittedMeterAsync("SEN-4471102", "128 As Nieves Road", 14_820.500m);

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RecordAsync(meter, new RecordReadingInput(15_120.750m, Note: "Read off the card"));
        }

        await using var read = fixture.CreateScope();

        var stored = await read.ServiceProvider.GetRequiredService<MeteringDbContext>()
            .MeterReadings.AsNoTracking().SingleAsync();

        // On a float column this is where 300.250 comes back as 300.24999999999997 and a bill stops
        // reconciling with the readings behind it.
        Assert.Equal(15_120.750m, stored.Reading);
        Assert.Equal(14_820.500m, stored.PreviousReading);
        Assert.Equal(300.250m, stored.Consumption);
        Assert.Equal(ReadingExceptionCode.None, stored.ExceptionCode);
    }

    [Fact]
    public async Task A_cycle_reads_every_fitted_meter_and_the_readings_explain_themselves()
    {
        var first = await AFittedMeterAsync("SEN-4471102", "128 As Nieves Road", 1_000m);
        var second = await AFittedMeterAsync("SEN-4471188", "87 Airport Road", 2_000m);

        await using (var scope = fixture.CreateScope())
        {
            var cycle = await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RunCycleAsync(new RunReadingCycleInput("2026-08", Seed: 4471));

            Assert.Equal(2, cycle.Recorded);
        }

        await using var read = fixture.CreateScope();

        var context = read.ServiceProvider.GetRequiredService<MeteringDbContext>();

        var readings = await context.MeterReadings.AsNoTracking().ToListAsync();
        var registers = await context.Meters.AsNoTracking().ToDictionaryAsync(meter => meter.Id, meter => meter.RegisterDigits);

        Assert.Equal([first, second], readings.Select(reading => reading.MeterId).Order().ToArray().Order().ToArray());

        // Every figure is arithmetic somebody can redo from the line itself, which is why the pair of
        // readings is stamped alongside the answer.
        Assert.All(
            readings.Where(reading => reading.Consumption is not null),
            reading => Assert.Equal(
                ConsumptionCalculator.Between(reading.PreviousReading!.Value, reading.Reading!.Value, registers[reading.MeterId]),
                new ConsumptionResult(reading.Consumption!.Value, reading.RolledOver)));
    }

    [Fact]
    public async Task The_database_refuses_a_second_reading_for_one_meter_in_one_cycle()
    {
        // WP-2.2's idempotency rule where it is actually guaranteed. The service checks the cycle
        // code first, so this inserts straight through the context to get past it — which is what a
        // retried request racing the first does.
        var meter = await AFittedMeterAsync("SEN-4471102", "128 As Nieves Road", 1_000m);

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RunCycleAsync(new RunReadingCycleInput("2026-08", Seed: 4471));
        }

        await using var second = fixture.CreateScope();

        var context = second.ServiceProvider.GetRequiredService<MeteringDbContext>();

        context.MeterReadings.Add(MeterReading.Record(
            await context.Meters.SingleAsync(candidate => candidate.Id == meter),
            ReadingBaseline.None,
            9_999m,
            DateTimeOffset.UtcNow,
            MeterReadingSource.Cycle,
            new RegistryActor("system", "system"),
            DateTimeOffset.UtcNow,
            "2026-08"));

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Equal("23505", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }

    [Fact]
    public async Task A_meter_can_still_be_read_by_hand_as_often_as_a_dispute_needs()
    {
        // The other half of the same unfiltered unique index: a manual reading holds NULL in
        // cycle_code, and Postgres treats NULLs in a unique index as distinct. Worth proving on the
        // provider that will hold them, because the rule rests entirely on that behaviour.
        var meter = await AFittedMeterAsync("SEN-4471102", "128 As Nieves Road", 0m);

        foreach (var dials in new[] { 100m, 200m, 300m })
        {
            await using var scope = fixture.CreateScope();

            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RecordAsync(meter, new RecordReadingInput(dials));
        }

        await using var read = fixture.CreateScope();

        Assert.Equal(3, await read.ServiceProvider.GetRequiredService<MeteringDbContext>().MeterReadings.CountAsync());
    }

    [Fact]
    public async Task Running_a_cycle_twice_is_refused_with_a_conflict_rather_than_doubling_the_register()
    {
        var meter = await AFittedMeterAsync("SEN-4471102", "128 As Nieves Road", 1_000m);

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RunCycleAsync(new RunReadingCycleInput("2026-08", Seed: 4471));
        }

        await using (var scope = fixture.CreateScope())
        {
            await Assert.ThrowsAsync<MeterWorkflowException>(() =>
                scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                    .RunCycleAsync(new RunReadingCycleInput("2026-08", Seed: 4471)));
        }

        await using var read = fixture.CreateScope();

        Assert.Equal(
            1,
            await read.ServiceProvider.GetRequiredService<MeteringDbContext>()
                .MeterReadings.CountAsync(reading => reading.MeterId == meter));
    }

    [Fact]
    public async Task A_reading_its_audit_entry_and_its_outbox_row_are_one_transaction()
    {
        // Invariants 1 and 2 on a real connection: three contexts, three schemas, one commit.
        var meter = await AFittedMeterAsync("SEN-4471102", "128 As Nieves Road", 0m);

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RecordAsync(meter, new RecordReadingInput(540m));
        }

        await using var read = fixture.CreateScope();

        var platform = read.ServiceProvider.GetRequiredService<Platform.Data.PlatformDbContext>();

        Assert.Single(await platform.AuditEntries
            .Where(entry => entry.Action == Platform.Audit.AuditActions.MeterReadingRecorded)
            .ToListAsync());
    }

    [Fact]
    public async Task A_meter_in_a_store_cannot_be_read()
    {
        // Failure path across a real transaction: nothing is written and the caller gets a conflict.
        Guid meter;

        await using (var scope = fixture.CreateScope())
        {
            meter = (await scope.ServiceProvider.GetRequiredService<IMeterService>()
                .RegisterAsync(new RegisterMeterInput("SEN-4471301", MeterType.SinglePhase)))
                .Meter.Id;
        }

        await using (var scope = fixture.CreateScope())
        {
            await Assert.ThrowsAsync<MeterWorkflowException>(() =>
                scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                    .RecordAsync(meter, new RecordReadingInput(540m)));
        }

        await using var read = fixture.CreateScope();

        Assert.Empty(await read.ServiceProvider.GetRequiredService<MeteringDbContext>().MeterReadings.ToListAsync());
    }
}
