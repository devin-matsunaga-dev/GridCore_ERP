namespace GridCore.Modules.Metering.Features.Readings;

/// <summary>One measured period as the averager sees it: units used, over a span of days.</summary>
/// <remarks>
/// A struct of three numbers rather than a <see cref="MeterReading"/>, so the arithmetic below can
/// be proven exhaustively with no database and no entity graph (CONVENTIONS.md rule C). The service
/// turns rows into these; this file turns these into an average.
/// </remarks>
/// <param name="Consumption">Units used over the period. Never negative — a rollover is already resolved.</param>
/// <param name="Start">When the period opened: the previous reading's date.</param>
/// <param name="End">When it closed: this reading's date.</param>
public readonly record struct UsagePeriod(decimal Consumption, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Average monthly consumption from a run of measured periods — the whole of WP-2.17's usage rule,
/// in one pure function.
/// </summary>
/// <remarks>
/// <para>
/// Pure and static, the call <see cref="ConsumptionCalculator"/> already made for the arithmetic a
/// bill is built on, and for the same reason: a deposit assessed on usage is money asked of a
/// customer, so the sum behind it must be provable in milliseconds with nothing spun up.
/// </para>
/// <para>
/// <b>Days, not readings.</b> The average is total consumption divided by the days those periods
/// actually span, scaled to <see cref="DaysPerMonth"/>. Dividing by a count of readings would give
/// a premise whose cycle was missed an average built from a two-month period counted as one month —
/// which inflates the assessment of exactly the customer who was hardest to read.
/// </para>
/// </remarks>
public static class UsageAverage
{
    /// <summary>
    /// Days in an average month: 365.25 ÷ 12, the same figure a utility's own tariff notes use.
    /// </summary>
    /// <remarks>
    /// Not 30, and not the length of the calendar month the assessment happens to fall in. A deposit
    /// worked out in February and the same one worked out in March must agree, and they cannot if
    /// the divisor is whichever month a rep opened the screen.
    /// </remarks>
    public const decimal DaysPerMonth = 30.4375m;

    /// <summary>Decimal places the average is reported to. Three, matching the register's own width.</summary>
    public const int DecimalPlaces = 3;

    /// <summary>
    /// What <paramref name="periods"/> work out to in an average month, or <see langword="null"/>
    /// when there is nothing to average.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A null rather than a zero for an empty run, and the distinction is load-bearing: a premise
    /// with no reading history has not been measured consuming nothing. WP-2.17's rule is that such
    /// a customer falls back to the schedule minimum, which is only expressible if "no history" and
    /// "no usage" are different answers.
    /// </para>
    /// <para>
    /// A period whose span is not positive is skipped rather than divided by. It should not happen —
    /// a reading is taken after the one before it — but a clock skew or a hand-entered date should
    /// cost one period, not the whole assessment.
    /// </para>
    /// </remarks>
    public static decimal? MonthlyOf(IEnumerable<UsagePeriod> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);

        var consumption = 0m;
        var days = 0m;

        foreach (var period in periods)
        {
            var span = (decimal)(period.End - period.Start).TotalDays;

            if (span <= 0m)
            {
                continue;
            }

            consumption += period.Consumption;
            days += span;
        }

        return days <= 0m ? null : Math.Round(consumption / days * DaysPerMonth, DecimalPlaces, MidpointRounding.AwayFromZero);
    }

    /// <summary>The whole days <paramref name="periods"/> cover, for the record the caller hands on.</summary>
    /// <remarks>
    /// Reported beside the average rather than left implicit, because "two months of average usage"
    /// is a claim, and the span it was drawn from is the evidence for it. Rounded to whole days: it
    /// is shown to a rep, not divided by.
    /// </remarks>
    public static int DaysCoveredBy(IEnumerable<UsagePeriod> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);

        var days = 0d;

        foreach (var period in periods)
        {
            var span = (period.End - period.Start).TotalDays;

            if (span > 0d)
            {
                days += span;
            }
        }

        return (int)Math.Round(days, MidpointRounding.AwayFromZero);
    }
}
