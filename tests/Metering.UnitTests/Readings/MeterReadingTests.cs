using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Metering.UnitTests.Readings;

/// <summary>
/// The reading aggregate on its own: what it refuses, and what it works out and writes down at the
/// moment a reading is taken. No database anywhere near it.
/// </summary>
public sealed class MeterReadingTests
{
    private static readonly DateTimeOffset Fitted = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset July = new(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset August = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly RegistryActor Crew = new("technician-1", "Jesse Atalig");

    private static Meter FittedMeter(decimal? installationReading = 0m, int registerDigits = 5)
    {
        var meter = Meter.Register("MTR-000001", "SEN-1", MeterType.SinglePhase, Crew, Fitted, registerDigits);

        meter.InstallAt(Guid.CreateVersion7(Fitted), Crew, Fitted, installationReading);

        return meter;
    }

    private static MeterReading Record(
        Meter meter,
        ReadingBaseline baseline,
        decimal? reading,
        DateTimeOffset readingDate,
        string? cycleCode = null) =>
        MeterReading.Record(meter, baseline, reading, readingDate, MeterReadingSource.Cycle, Crew, August, cycleCode);

    [Fact]
    public void A_reading_stamps_the_premise_the_meter_was_measuring()
    {
        // Stamped rather than read off the meter later: a removal clears the meter's own column, and
        // "what did this premise use in March" has to survive the exchange.
        var meter = FittedMeter();

        var reading = Record(meter, ReadingBaseline.None, 540m, July);

        Assert.Equal(meter.ServiceLocationId, reading.ServiceLocationId);
        Assert.Equal(meter.Id, reading.MeterId);
    }

    [Fact]
    public void The_first_reading_at_a_premise_is_measured_from_the_installation_reading()
    {
        // WP-2.1's whole reason for stamping InstallationReading: a meter that has been round the
        // island and back must not bill its new customer for the last one's usage.
        var meter = FittedMeter(installationReading: 14_820.500m);

        var reading = Record(meter, new ReadingBaseline(14_820.500m, Fitted, null), 15_120.750m, July);

        Assert.Equal(300.250m, reading.Consumption);
        Assert.Equal(14_820.500m, reading.PreviousReading);
    }

    [Fact]
    public void A_meter_fitted_without_a_reading_measures_nothing_the_first_time()
    {
        // Not zero consumption, which would be a zero-usage exception and a bill for nothing. There
        // is genuinely no previous figure, and the line says so.
        var reading = Record(FittedMeter(installationReading: null), ReadingBaseline.None, 540m, July);

        Assert.Null(reading.Consumption);
        Assert.Null(reading.PreviousReading);
        Assert.Equal(ReadingExceptionCode.None, reading.ExceptionCode);
    }

    [Fact]
    public void A_rollover_is_worked_out_and_recorded_as_one()
    {
        var reading = Record(FittedMeter(), new ReadingBaseline(99_850m, July, null), 120m, August);

        Assert.Equal(270m, reading.Consumption);
        Assert.True(reading.RolledOver);
    }

    [Fact]
    public void A_missing_read_carries_no_reading_no_consumption_and_no_previous_dials()
    {
        // The line records that somebody tried and failed. Keeping a previous reading on it would
        // suggest a period was measured when none was.
        var reading = Record(FittedMeter(), new ReadingBaseline(14_820.500m, July, 18m), null, August);

        Assert.Null(reading.Reading);
        Assert.Null(reading.Consumption);
        Assert.Null(reading.PreviousReading);
        Assert.Null(reading.PreviousReadingDate);
        Assert.False(reading.RolledOver);
        Assert.Equal(ReadingExceptionCode.MissingRead, reading.ExceptionCode);
        Assert.True(reading.IsException);
    }

    [Fact]
    public void The_daily_figure_is_the_consumption_over_the_period()
    {
        var reading = Record(FittedMeter(), new ReadingBaseline(0m, July, null), 620m, August);

        Assert.Equal(31, reading.Days);
        Assert.Equal(20m, reading.DailyConsumption);
    }

    [Fact]
    public void A_reading_off_a_faulty_meter_is_still_recorded()
    {
        // Faulty counts as fitted (WP-2.1): the device is on the wall, still holds the premise, and
        // is read on the next visit — which is often how the fault is proved.
        var meter = FittedMeter();

        meter.ChangeStatus(MeterStatus.Faulty, Crew, July, "Dials suspected slow");

        var reading = Record(meter, new ReadingBaseline(0m, Fitted, null), 12m, August);

        Assert.Equal(12m, reading.Reading);
    }

    [Theory]
    [InlineData(MeterStatus.InStore)]
    [InlineData(MeterStatus.Retired)]
    public void A_meter_that_is_not_on_a_wall_cannot_have_measured_anything(MeterStatus status)
    {
        // Failure path. A 409, not a 400: whether this meter could have produced a reading depends
        // on where it is now, which no validator at the edge can see.
        var meter = Meter.Register("MTR-000001", "SEN-1", MeterType.SinglePhase, Crew, Fitted);

        if (status is MeterStatus.Retired)
        {
            meter.ChangeStatus(MeterStatus.Retired, Crew, Fitted, "Failed bench check");
        }

        var refused = Assert.Throws<MeterWorkflowException>(() => Record(meter, ReadingBaseline.None, 540m, July));

        Assert.Contains(status.ToString(), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_removed_meter_cannot_be_read_either()
    {
        var meter = FittedMeter();

        meter.Remove(Crew, July, "Tenant moved out");

        Assert.Throws<MeterWorkflowException>(() => Record(meter, ReadingBaseline.None, 540m, August));
    }

    [Fact]
    public void A_reading_dated_before_the_last_one_is_refused()
    {
        // Failure path, and a 409: out-of-order readings would make every consumption figure after
        // them arithmetic on the wrong pair of dials.
        var refused = Assert.Throws<MeterWorkflowException>(() =>
            Record(FittedMeter(), new ReadingBaseline(500m, August, null), 600m, July));

        Assert.Contains("2026-08-01", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reading_dated_the_same_day_as_the_last_one_is_allowed() =>
        // A re-read after a query is legitimate. Only going backwards is refused.
        Assert.Equal(600m, Record(FittedMeter(), new ReadingBaseline(500m, July, null), 600m, July).Reading);

    [Fact]
    public void A_reading_dated_in_the_future_is_refused() =>
        // It would sit at the head of the register and make every reading after it look like the
        // dials had gone backwards.
        Assert.Throws<MeterValidationException>(() =>
            MeterReading.Record(FittedMeter(), ReadingBaseline.None, 540m, August.AddDays(1), MeterReadingSource.Manual, Crew, August));

    [Fact]
    public void A_reading_finer_than_the_register_stores_is_refused_rather_than_rounded()
    {
        var refused = Assert.Throws<MeterValidationException>(() =>
            Record(FittedMeter(), ReadingBaseline.None, 540.0001m, July));

        Assert.Contains("3 decimal places", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reading_the_meters_own_register_cannot_display_is_refused()
    {
        // The register width is per meter, so this is a question about this device rather than about
        // the column: 120 000 is fine on a six-digit intake and impossible on a domestic five.
        var refused = Assert.Throws<MeterValidationException>(() =>
            Record(FittedMeter(registerDigits: 5), ReadingBaseline.None, 120_000m, July));

        Assert.Contains("5-digit register", refused.Message, StringComparison.Ordinal);

        Assert.Equal(120_000m, Record(FittedMeter(registerDigits: 6), ReadingBaseline.None, 120_000m, July).Reading);
    }

    [Fact]
    public void A_negative_reading_is_refused() =>
        Assert.Throws<MeterValidationException>(() => Record(FittedMeter(), ReadingBaseline.None, -1m, July));

    [Fact]
    public void A_reading_must_name_who_recorded_it() =>
        Assert.Throws<MeterValidationException>(() =>
            MeterReading.Record(FittedMeter(), ReadingBaseline.None, 540m, July, MeterReadingSource.Manual, new RegistryActor("  ", null), August));

    [Fact]
    public void A_source_GridCore_does_not_declare_is_refused() =>
        Assert.Throws<MeterValidationException>(() =>
            MeterReading.Record(FittedMeter(), ReadingBaseline.None, 540m, July, (MeterReadingSource)99, Crew, August));

    [Fact]
    public void A_cycle_code_and_a_note_are_trimmed_and_capped()
    {
        var reading = MeterReading.Record(
            FittedMeter(),
            ReadingBaseline.None,
            540m,
            July,
            MeterReadingSource.Cycle,
            Crew,
            August,
            cycleCode: "  2026-07  ",
            note: new string('n', MeterReading.NoteLength + 50));

        Assert.Equal("2026-07", reading.CycleCode);
        Assert.Equal(MeterReading.NoteLength, reading.Note!.Length);
    }

    [Fact]
    public void A_manual_reading_belongs_to_no_cycle() =>
        Assert.Null(MeterReading.Record(FittedMeter(), ReadingBaseline.None, 540m, July, MeterReadingSource.Manual, Crew, August).CycleCode);

    [Fact]
    public void A_high_usage_reading_is_flagged_against_what_the_premise_normally_uses()
    {
        var reading = Record(FittedMeter(), new ReadingBaseline(0m, July, TypicalDailyConsumption: 18m), 3_100m, August);

        Assert.Equal(ReadingExceptionCode.HighUsage, reading.ExceptionCode);
        Assert.True(reading.IsException);
    }

    [Fact]
    public void An_ordinary_reading_is_not_on_the_worklist()
    {
        var reading = Record(FittedMeter(), new ReadingBaseline(0m, July, TypicalDailyConsumption: 18m), 620m, August);

        Assert.Equal(ReadingExceptionCode.None, reading.ExceptionCode);
        Assert.False(reading.IsException);
    }
}
