using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Metering.UnitTests.Readings;

/// <summary>
/// What a new reading is measured against. Pure, so the rules that decide it — the fitting boundary,
/// the fallback to the installation reading, whether a premise's usage profile survives an
/// exchange — are all provable without a database.
/// </summary>
public sealed class ReadingBaselineTests
{
    private static readonly DateTimeOffset Fitted = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly RegistryActor Crew = new("technician-1", "Jesse Atalig");

    private static readonly Guid Premise = Guid.CreateVersion7(Fitted);

    private static Meter FittedMeter(
        string meterNumber = "MTR-000001",
        string serialNumber = "SEN-1",
        decimal? installationReading = 0m,
        DateTimeOffset? fittedAt = null,
        Guid? premise = null)
    {
        var when = fittedAt ?? Fitted;
        var meter = Meter.Register(meterNumber, serialNumber, MeterType.SinglePhase, Crew, when);

        meter.InstallAt(premise ?? Premise, Crew, when, installationReading);

        return meter;
    }

    private static MeterReading Reading(Meter meter, decimal? dials, DateTimeOffset on, ReadingBaseline? baseline = null) =>
        MeterReading.Record(meter, baseline ?? ReadingBaseline.None, dials, on, MeterReadingSource.Cycle, Crew, on);

    private static DateTimeOffset Month(int month) => new(2026, month, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A date a whole number of days after the meter was fitted, so periods are equal lengths.</summary>
    private static DateTimeOffset Day(int day) => Fitted.AddDays(day);

    [Fact]
    public void With_no_history_the_baseline_is_what_the_meter_read_when_it_was_fitted()
    {
        var baseline = ReadingBaseline.From(FittedMeter(installationReading: 14_820.500m), []);

        Assert.Equal(14_820.500m, baseline.Reading);
        Assert.Equal(Fitted, baseline.ReadAt);
        Assert.Null(baseline.TypicalDailyConsumption);
    }

    [Fact]
    public void A_meter_in_a_store_has_no_baseline_at_all() =>
        Assert.Equal(
            ReadingBaseline.None,
            ReadingBaseline.From(Meter.Register("MTR-000001", "SEN-1", MeterType.SinglePhase, Crew, Fitted), []));

    [Fact]
    public void The_most_recent_reading_off_this_meter_wins()
    {
        var meter = FittedMeter();

        var july = Reading(meter, 620m, Month(7));
        var august = Reading(meter, 1_240m, Month(8));

        // Newest first, as the register hands them over.
        var baseline = ReadingBaseline.From(meter, [august, july]);

        Assert.Equal(1_240m, baseline.Reading);
        Assert.Equal(Month(8), baseline.ReadAt);
    }

    [Fact]
    public void A_missing_read_is_skipped_and_the_last_real_one_is_measured_from()
    {
        // Measuring from a missing read would report a whole period's consumption as nothing, and
        // then the period after it as double.
        var meter = FittedMeter();

        var july = Reading(meter, 620m, Month(7));
        var august = Reading(meter, null, Month(8));

        var baseline = ReadingBaseline.From(meter, [august, july]);

        Assert.Equal(620m, baseline.Reading);
        Assert.Equal(Month(7), baseline.ReadAt);
    }

    [Fact]
    public void Another_meters_dials_are_never_measured_from()
    {
        // The case an exchange creates. Dials do not carry over: the new meter starts wherever its
        // own register stands, and subtracting the old meter's last reading from it would bill the
        // difference between two unrelated devices.
        var outgoing = FittedMeter("MTR-000001", "SEN-1");
        var previousReading = Reading(outgoing, 99_000m, Month(7));

        var incoming = FittedMeter("MTR-000002", "SEN-2", installationReading: 12.500m, fittedAt: Month(7).AddDays(2));

        var baseline = ReadingBaseline.From(incoming, [previousReading]);

        Assert.Equal(12.500m, baseline.Reading);
        Assert.Equal(Month(7).AddDays(2), baseline.ReadAt);
    }

    [Fact]
    public void A_reading_from_before_this_meter_was_refitted_is_not_measured_from()
    {
        // Same device, second stint at the premise, with a spell in the yard in between. Its old
        // dials are not where its register stands now.
        var meter = FittedMeter();
        var beforeRemoval = Reading(meter, 5_000m, Month(3));

        meter.Remove(Crew, Month(4), "Exchanged for a fault");
        meter.ChangeStatus(MeterStatus.InStore, Crew, Month(5), "Passed bench check");
        meter.InstallAt(Premise, Crew, Month(6), 90m);

        var baseline = ReadingBaseline.From(meter, [beforeRemoval]);

        Assert.Equal(90m, baseline.Reading);
        Assert.Equal(Month(6), baseline.ReadAt);
    }

    [Fact]
    public void Readings_from_another_premise_are_ignored_entirely()
    {
        var meter = FittedMeter();
        var elsewhere = Reading(FittedMeter("MTR-000002", "SEN-2", premise: Guid.CreateVersion7()), 4_000m, Month(7));

        var baseline = ReadingBaseline.From(meter, [elsewhere]);

        Assert.Equal(0m, baseline.Reading);
        Assert.Null(baseline.TypicalDailyConsumption);
    }

    [Fact]
    public void The_usage_profile_is_averaged_over_the_premises_recent_readings()
    {
        var meter = FittedMeter();

        // Equal thirty-day periods, so the arithmetic is legible: 600 units is 20 a day, 300 is 10.
        var first = Reading(meter, 600m, Day(30), new ReadingBaseline(0m, Day(0), null));
        var second = Reading(meter, 900m, Day(60), new ReadingBaseline(600m, Day(30), null));

        var baseline = ReadingBaseline.From(meter, [second, first]);

        Assert.Equal(15m, baseline.TypicalDailyConsumption);
    }

    [Fact]
    public void The_usage_profile_survives_a_meter_exchange()
    {
        // Deliberately unlike the dial reading above. What a house uses belongs to the house, not to
        // the device on its wall — so a premise keeps its high-usage baseline through an exchange
        // rather than going unwatched for a cycle, which is exactly when a wrongly fitted or
        // mis-read meter is most likely.
        var outgoing = FittedMeter("MTR-000001", "SEN-1");
        var itsReading = Reading(outgoing, 600m, Day(30), new ReadingBaseline(0m, Day(0), null));

        var incoming = FittedMeter("MTR-000002", "SEN-2", installationReading: 0m, fittedAt: Day(32));

        var baseline = ReadingBaseline.From(incoming, [itsReading]);

        Assert.Equal(0m, baseline.Reading);
        Assert.Equal(20m, baseline.TypicalDailyConsumption);
    }

    [Fact]
    public void A_row_supplied_twice_does_not_weight_the_average_twice()
    {
        // The service fetches the exact previous reading separately from the premise window, so the
        // same row legitimately arrives in both.
        var meter = FittedMeter();

        var first = Reading(meter, 600m, Day(30), new ReadingBaseline(0m, Day(0), null));
        var second = Reading(meter, 1_800m, Day(60), new ReadingBaseline(600m, Day(30), null));

        Assert.Equal(
            ReadingBaseline.From(meter, [second, first]).TypicalDailyConsumption,
            ReadingBaseline.From(meter, [second, second, first]).TypicalDailyConsumption);
    }

    [Fact]
    public void Missing_reads_contribute_nothing_to_the_usage_profile()
    {
        var meter = FittedMeter();

        var read = Reading(meter, 600m, Day(30), new ReadingBaseline(0m, Day(0), null));
        var unread = Reading(meter, null, Day(60));

        Assert.Equal(20m, ReadingBaseline.From(meter, [unread, read]).TypicalDailyConsumption);
    }
}
