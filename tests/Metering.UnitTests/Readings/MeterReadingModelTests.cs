using GridCore.Modules.Metering.Data;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Metering.UnitTests.Infrastructure;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Metering.UnitTests.Readings;

/// <summary>The reading register as EF actually builds it.</summary>
public sealed class MeterReadingModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly RegistryActor Crew = new("technician-1", "Jesse Atalig");

    private static Meter FittedMeter(Guid premise, string meterNumber = "MTR-000001", string serialNumber = "SEN-1")
    {
        var meter = Meter.Register(meterNumber, serialNumber, MeterType.SinglePhase, Crew, Now);

        meter.InstallAt(premise, Crew, Now, 0m);

        return meter;
    }

    private static MeterReading Reading(Meter meter, decimal? dials, string? cycleCode) =>
        MeterReading.Record(meter, ReadingBaseline.None, dials, Now, MeterReadingSource.Cycle, Crew, Now, cycleCode);

    [Fact]
    public void The_readings_table_is_named_in_snake_case_in_the_modules_own_schema()
    {
        using var host = new MeteringTestHost();
        using var context = host.NewMeteringContext();

        Assert.Equal("meter_readings", context.Model.FindEntityType(typeof(MeterReading))!.GetTableName());
        Assert.Equal(MeteringDbContext.SchemaName, context.Model.FindEntityType(typeof(MeterReading))!.GetSchema() ?? context.Model.GetDefaultSchema());
    }

    [Theory]
    [InlineData(nameof(MeterReading.Days))]
    [InlineData(nameof(MeterReading.DailyConsumption))]
    [InlineData(nameof(MeterReading.IsException))]
    public void The_derived_figures_are_not_columns(string property)
    {
        // They are computed from what is stored. Mapped, EF would want backing fields it cannot find
        // and the model would fail to build at startup rather than in a test.
        using var host = new MeteringTestHost();
        using var context = host.NewMeteringContext();

        var reading = context.Model.FindEntityType(typeof(MeterReading))!;

        Assert.Null(reading.FindProperty(property));
        Assert.Null(reading.FindNavigation(property));
    }

    [Fact]
    public void The_premise_is_a_plain_column_with_no_foreign_key()
    {
        // Customers is another module over another schema, so the database cannot enforce this and
        // must not pretend to.
        using var host = new MeteringTestHost();
        using var context = host.NewMeteringContext();

        var reading = context.Model.FindEntityType(typeof(MeterReading))!;

        Assert.NotNull(reading.FindProperty(nameof(MeterReading.ServiceLocationId)));
        Assert.DoesNotContain(
            reading.GetForeignKeys(),
            key => key.Properties.Any(property => property.Name == nameof(MeterReading.ServiceLocationId)));
    }

    [Fact]
    public void The_meter_is_a_real_foreign_key_because_it_is_this_modules_own_row()
    {
        using var host = new MeteringTestHost();
        using var context = host.NewMeteringContext();

        Assert.Contains(
            context.Model.FindEntityType(typeof(MeterReading))!.GetForeignKeys(),
            key => key.Properties.Any(property => property.Name == nameof(MeterReading.MeterId)));
    }

    [Fact]
    public void A_reading_is_not_a_navigation_collection_on_the_meter() =>
        // WP-1.4's rule applied to the register that will grow fastest in GridCore: recording one
        // reading must not load a decade of them.
        Assert.DoesNotContain(
            new MeteringTestHost().NewMeteringContext().Model.FindEntityType(typeof(Meter))!.GetNavigations(),
            navigation => navigation.TargetEntityType.ClrType == typeof(MeterReading));

    [Fact]
    public void The_cycle_index_carries_no_filter_at_all()
    {
        // Asserted rather than assumed, WP-1.2's lesson: a filter here would be a SQL string the
        // compiler never reads. It needs none — a manual reading holds NULL in cycle_code, and NULLs
        // in a unique index are distinct on both providers.
        using var host = new MeteringTestHost();
        using var context = host.NewMeteringContext();

        var index = context.Model
            .FindEntityType(typeof(MeterReading))!
            .GetIndexes()
            .Single(candidate => candidate.GetDatabaseName() == "ux_meter_readings_meter_cycle");

        Assert.True(index.IsUnique);
        Assert.Null(index.GetFilter());
        Assert.Equal(
            [nameof(MeterReading.MeterId), nameof(MeterReading.CycleCode)],
            index.Properties.Select(property => property.Name).ToArray());
    }

    [Fact]
    public async Task One_meter_cannot_be_read_twice_in_the_same_cycle()
    {
        // Failure path at the database. The service checks first; this is what makes the rule true
        // even when two requests race.
        using var host = new MeteringTestHost();
        await using var context = host.NewMeteringContext();

        var premise = Guid.CreateVersion7(Now);
        var meter = FittedMeter(premise);

        context.Meters.Add(meter);
        context.MeterReadings.AddRange(Reading(meter, 540m, "2026-08"), Reading(meter, 600m, "2026-08"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_meter_can_be_read_by_hand_as_often_as_a_dispute_needs()
    {
        // The other half of the same unfiltered unique index: a manual reading holds NULL, and NULLs
        // are distinct on both Postgres and SQLite.
        using var host = new MeteringTestHost();
        await using var context = host.NewMeteringContext();

        var meter = FittedMeter(Guid.CreateVersion7(Now));

        context.Meters.Add(meter);
        context.MeterReadings.AddRange(Reading(meter, 540m, null), Reading(meter, 600m, null), Reading(meter, 620m, null));

        await context.SaveChangesAsync();

        Assert.Equal(3, await context.MeterReadings.CountAsync());
    }

    [Fact]
    public async Task Two_meters_can_be_read_in_the_same_cycle()
    {
        using var host = new MeteringTestHost();
        await using var context = host.NewMeteringContext();

        var first = FittedMeter(Guid.CreateVersion7(Now), "MTR-000001", "SEN-1");
        var second = FittedMeter(Guid.CreateVersion7(Now), "MTR-000002", "SEN-2");

        context.Meters.AddRange(first, second);
        context.MeterReadings.AddRange(Reading(first, 540m, "2026-08"), Reading(second, 600m, "2026-08"));

        await context.SaveChangesAsync();

        Assert.Equal(2, await context.MeterReadings.CountAsync());
    }

    [Fact]
    public async Task A_reading_and_its_consumption_keep_their_three_decimal_places()
    {
        // decimal, never double. On a float column this is where 300.250 comes back as
        // 300.24999999999997 and a bill stops reconciling.
        using var host = new MeteringTestHost();

        var premise = Guid.CreateVersion7(Now);
        var meter = FittedMeter(premise);

        var reading = MeterReading.Record(
            meter,
            new ReadingBaseline(14_820.500m, Now, null),
            15_120.750m,
            Now,
            MeterReadingSource.Manual,
            Crew,
            Now);

        await using (var writing = host.NewMeteringContext())
        {
            writing.Meters.Add(meter);
            writing.MeterReadings.Add(reading);
            await writing.SaveChangesAsync();
        }

        await using var read = host.NewMeteringContext();

        var stored = await read.MeterReadings.SingleAsync();

        Assert.Equal(15_120.750m, stored.Reading);
        Assert.Equal(14_820.500m, stored.PreviousReading);
        Assert.Equal(300.250m, stored.Consumption);
    }

    [Fact]
    public void The_register_width_is_a_required_column_with_no_database_default()
    {
        // A register width guessed by the schema is one nobody transcribed off a nameplate, and
        // rollover arithmetic built on a guess is worse than arithmetic that refuses to run. The
        // migration backfills the meters that predate the column.
        using var host = new MeteringTestHost();
        using var context = host.NewMeteringContext();

        var digits = context.Model.FindEntityType(typeof(Meter))!.FindProperty(nameof(Meter.RegisterDigits))!;

        Assert.False(digits.IsNullable);
        Assert.Null(digits.GetDefaultValueSql());
    }

    [Fact]
    public void The_registers_capacity_is_not_a_column() =>
        Assert.Null(
            new MeteringTestHost().NewMeteringContext().Model
                .FindEntityType(typeof(Meter))!
                .FindProperty(nameof(Meter.RegisterCapacity)));
}
