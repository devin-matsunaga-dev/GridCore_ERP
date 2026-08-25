using GridCore.Modules.Metering.Features.Readings;

namespace GridCore.Modules.Metering.UnitTests.Readings;

/// <summary>
/// Which readings are held for somebody to look at. The judgement half of a reading, kept apart from
/// the arithmetic half so each can be retuned without disturbing the other.
/// </summary>
public sealed class ReadingAssessmentTests
{
    [Fact]
    public void A_meter_nobody_could_read_is_a_missing_read() =>
        Assert.Equal(
            ReadingExceptionCode.MissingRead,
            ReadingAssessment.Classify(reading: null, consumption: null, days: 30, typicalDailyConsumption: 18m));

    [Fact]
    public void A_missing_read_stays_a_missing_read_even_where_there_is_nothing_to_compare_it_to() =>
        // Checked before everything else on purpose: it has no consumption either, and reporting
        // "nothing to compare" would lose the fact that nobody got to the meter at all.
        Assert.Equal(
            ReadingExceptionCode.MissingRead,
            ReadingAssessment.Classify(reading: null, consumption: null, days: 30, typicalDailyConsumption: null));

    [Fact]
    public void A_first_reading_measures_nothing_and_is_not_an_exception() =>
        // A meter fitted with no reading taken has no previous dials. There is no anomaly in a meter
        // that has only been read once, and flagging one would put every new connection on the
        // worklist.
        Assert.Equal(
            ReadingExceptionCode.None,
            ReadingAssessment.Classify(reading: 540m, consumption: null, days: 30, typicalDailyConsumption: null));

    [Fact]
    public void Dials_that_have_not_moved_are_a_zero_usage_exception() =>
        Assert.Equal(
            ReadingExceptionCode.ZeroUsage,
            ReadingAssessment.Classify(reading: 22_101m, consumption: 0m, days: 30, typicalDailyConsumption: 18m));

    [Fact]
    public void Zero_usage_is_flagged_even_at_a_premise_with_no_history() =>
        // Both explanations — an empty property and a stopped meter — are worth a look, and only one
        // of them is safe to bill.
        Assert.Equal(
            ReadingExceptionCode.ZeroUsage,
            ReadingAssessment.Classify(reading: 22_101m, consumption: 0m, days: 30, typicalDailyConsumption: null));

    [Fact]
    public void An_ordinary_month_is_not_an_exception() =>
        Assert.Equal(
            ReadingExceptionCode.None,
            ReadingAssessment.Classify(reading: 1_000m, consumption: 600m, days: 30, typicalDailyConsumption: 18m));

    [Fact]
    public void Far_more_than_the_premise_usually_uses_is_a_high_usage_exception() =>
        // 18 a day is usual, this is 100 a day.
        Assert.Equal(
            ReadingExceptionCode.HighUsage,
            ReadingAssessment.Classify(reading: 4_000m, consumption: 3_000m, days: 30, typicalDailyConsumption: 18m));

    [Fact]
    public void The_threshold_is_exclusive_so_exactly_the_multiple_is_still_ordinary()
    {
        // 18 × 3 × 30 days = 1620, exactly at the line. Pinned because "just over" and "just under"
        // are where a retuned threshold silently changes which readings reach the worklist.
        const decimal Typical = 18m;
        var atTheLine = Typical * ReadingAssessment.HighUsageMultiple * 30m;

        Assert.Equal(ReadingExceptionCode.None, ReadingAssessment.Classify(9_000m, atTheLine, 30, Typical));
        Assert.Equal(ReadingExceptionCode.HighUsage, ReadingAssessment.Classify(9_000m, atTheLine + 0.001m, 30, Typical));
    }

    [Fact]
    public void A_long_period_is_judged_per_day_and_not_per_reading()
    {
        // The reason everything here is per day. Two months at exactly the usual rate is twice the
        // usual number of units; measured per reading it would look like a leak.
        Assert.Equal(
            ReadingExceptionCode.None,
            ReadingAssessment.Classify(reading: 2_000m, consumption: 1_080m, days: 60, typicalDailyConsumption: 18m));
    }

    [Fact]
    public void A_short_period_of_heavy_use_is_still_caught()
    {
        // And the other side of it: a fortnight that used a month's worth is per-day high, even
        // though the total looks ordinary.
        Assert.Equal(
            ReadingExceptionCode.HighUsage,
            ReadingAssessment.Classify(reading: 2_000m, consumption: 1_080m, days: 7, typicalDailyConsumption: 18m));
    }

    [Fact]
    public void A_premise_with_no_usage_history_gets_no_high_usage_exception() =>
        // Nothing to judge against. Flagging here would mean building a baseline out of nothing and
        // then holding a reading for failing it.
        Assert.Equal(
            ReadingExceptionCode.None,
            ReadingAssessment.Classify(reading: 90_000m, consumption: 80_000m, days: 30, typicalDailyConsumption: null));

    [Fact]
    public void A_baseline_of_zero_is_treated_as_no_baseline() =>
        // A premise whose only history is zero-usage months has no usable typical figure, and
        // dividing by it would flag the first unit it ever uses.
        Assert.Equal(
            ReadingExceptionCode.None,
            ReadingAssessment.Classify(reading: 100m, consumption: 100m, days: 30, typicalDailyConsumption: 0m));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_period_of_no_days_is_clamped_rather_than_dividing_by_zero(int days) =>
        Assert.Equal(
            ReadingExceptionCode.HighUsage,
            ReadingAssessment.Classify(reading: 1_000m, consumption: 500m, days, typicalDailyConsumption: 18m));

    [Fact]
    public void Two_readings_on_the_same_day_count_as_one_day() =>
        // Not zero: a re-read after a dispute is a legitimate thing to do, and dividing by zero is
        // worse than treating it as a short period.
        Assert.Equal(1, ReadingAssessment.DaysBetween(new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 1, 16, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void A_month_between_readings_is_counted_in_whole_days() =>
        Assert.Equal(
            31,
            ReadingAssessment.DaysBetween(
                new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero)));
}
