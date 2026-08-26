using GridCore.Modules.Metering.Features.Readings;

namespace GridCore.Modules.Metering.UnitTests.Readings;

/// <summary>
/// Average monthly consumption from a run of measured periods (WP-2.17). What these prove: the
/// average is drawn from the DAYS the periods span rather than from how many of them there are, a
/// premise with no history answers null rather than zero, and a nonsense period costs one period
/// instead of the whole assessment.
/// </summary>
/// <remarks>
/// Pure arithmetic, no host, no database — the tier this belongs in, because a deposit assessed on
/// usage is money asked of a customer and the sum behind it has to be provable in milliseconds.
/// </remarks>
public class UsageAverageTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_periods_is_null_rather_than_zero() =>
        // The distinction the whole rule rests on: a premise nobody has read has not been measured
        // using nothing, and a zero here would assess every new connection at nothing at all.
        Assert.Null(UsageAverage.MonthlyOf([]));

    [Fact]
    public void One_month_of_readings_averages_to_that_month()
    {
        // 300 units over 30.4375 days is exactly one average month's worth.
        var average = UsageAverage.MonthlyOf([Period(300m, 0, UsageAverage.DaysPerMonth)]);

        Assert.Equal(300.000m, average);
    }

    [Fact]
    public void Twelve_months_of_steady_use_averages_to_one_month_of_it()
    {
        var periods = Enumerable.Range(0, 12)
            .Select(month => Period(250m, month * UsageAverage.DaysPerMonth, UsageAverage.DaysPerMonth))
            .ToList();

        Assert.Equal(250.000m, UsageAverage.MonthlyOf(periods));
    }

    [Fact]
    public void A_missed_cycle_is_averaged_over_the_days_it_actually_covers()
    {
        // One reading covering two months' worth of days. Dividing by a count of READINGS would call
        // this 600 a month and double the deposit of the customer who was hardest to read; dividing
        // by the days it spans calls it 300, which is what the premise actually used.
        var average = UsageAverage.MonthlyOf([Period(600m, 0, UsageAverage.DaysPerMonth * 2)]);

        Assert.Equal(300.000m, average);
    }

    [Fact]
    public void A_period_with_no_span_is_skipped_rather_than_divided_by()
    {
        // A clock skew or a hand-entered date. It costs one period, not the whole assessment.
        var average = UsageAverage.MonthlyOf(
        [
            new UsagePeriod(999m, Start, Start),
            Period(300m, 0, UsageAverage.DaysPerMonth),
        ]);

        Assert.Equal(300.000m, average);
    }

    [Fact]
    public void Every_period_being_span_less_reads_as_no_history() =>
        Assert.Null(UsageAverage.MonthlyOf([new UsagePeriod(500m, Start, Start)]));

    [Fact]
    public void The_average_is_rounded_to_the_registers_own_width()
    {
        // 100 units over 7 days scales to 434.821428... a month; three places, halves away from zero.
        var average = UsageAverage.MonthlyOf([Period(100m, 0, 7m)]);

        Assert.Equal(434.821m, average);
    }

    [Fact]
    public void The_days_covered_are_reported_as_whole_days() =>
        Assert.Equal(61, UsageAverage.DaysCoveredBy(
        [
            Period(100m, 0, UsageAverage.DaysPerMonth),
            Period(100m, UsageAverage.DaysPerMonth, UsageAverage.DaysPerMonth),
        ]));

    [Fact]
    public void A_span_less_period_covers_no_days() =>
        Assert.Equal(0, UsageAverage.DaysCoveredBy([new UsagePeriod(100m, Start, Start)]));

    private static UsagePeriod Period(decimal consumption, decimal offsetDays, decimal spanDays) =>
        new(
            consumption,
            Start.AddDays((double)offsetDays),
            Start.AddDays((double)(offsetDays + spanDays)));
}
