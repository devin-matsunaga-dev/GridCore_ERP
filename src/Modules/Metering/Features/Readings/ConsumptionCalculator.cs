using GridCore.Modules.Metering.Features.Shared;

namespace GridCore.Modules.Metering.Features.Readings;

/// <summary>What a pair of dial readings works out to.</summary>
/// <param name="Consumption">Units used between the two readings. Never negative.</param>
/// <param name="RolledOver">
/// Whether the register wrapped past its last digit between them. Recorded rather than inferred
/// later: the same pair of numbers means something different on a five-digit register and on a
/// seven-digit one, and the meter's width can be corrected afterwards.
/// </param>
public readonly record struct ConsumptionResult(decimal Consumption, bool RolledOver);

/// <summary>
/// Consumption from a pair of dial readings, including the moment the register rolls over.
/// </summary>
/// <remarks>
/// <para>
/// Pure and static, taking numbers and returning numbers. That is deliberate: this is the
/// arithmetic every bill in GridCore is ultimately built on, so it is the thing that must be
/// testable exhaustively in milliseconds with no database anywhere near it (CONVENTIONS.md's ⚡
/// rules). Everything that needs a row — which reading came before, which premise it was at —
/// belongs to <see cref="ReadingBaseline"/> and the service.
/// </para>
/// <para>
/// <b>Rollover.</b> A mechanical or electronic register carries a fixed number of whole digits and
/// counts back to zero when it fills — a five-digit meter goes 99 999 → 00 000, not to 100 000. So
/// a current reading <i>below</i> the previous one is the normal end of a register's cycle, not an
/// error, and the units used are what remained to the top plus what has been counted since. Billing
/// the difference directly would credit the customer a whole register's worth of energy.
/// </para>
/// <para>
/// A reading that has gone backwards for a bad reason — a digit transposed, the wrong meter read —
/// is indistinguishable here from a genuine rollover, and this deliberately does not try to guess.
/// It reports the arithmetic; <see cref="ReadingAssessment"/> is what notices that the answer is
/// implausible and raises a high-usage exception for somebody to look at. Two concerns, two places.
/// </para>
/// </remarks>
public static class ConsumptionCalculator
{
    /// <summary>Fewest whole digits a register may carry. Below this a meter would roll over monthly.</summary>
    public const int MinRegisterDigits = 4;

    /// <summary>Most whole digits a register may carry.</summary>
    public const int MaxRegisterDigits = 9;

    /// <summary>
    /// What a register of <paramref name="registerDigits"/> whole digits counts up to before it
    /// returns to zero — <c>10^digits</c>, so a five-digit register holds 0 to 99 999.999 and
    /// wraps at 100 000.
    /// </summary>
    /// <exception cref="MeterValidationException">The width is outside what GridCore stores.</exception>
    public static decimal CapacityOf(int registerDigits)
    {
        RequireWidth(registerDigits);

        // Multiplied out rather than Math.Pow: this is a decimal answer feeding decimal arithmetic,
        // and going through double to get it is exactly the kind of rounding CONVENTIONS.md keeps
        // out of anything that ends up on a bill.
        var capacity = 1m;

        for (var digit = 0; digit < registerDigits; digit++)
        {
            capacity *= 10m;
        }

        return capacity;
    }

    /// <summary>Whether <paramref name="reading"/> is a value a register that wide can display.</summary>
    public static bool Fits(decimal reading, int registerDigits) =>
        reading >= 0m && reading < CapacityOf(registerDigits);

    /// <summary>
    /// Units consumed between two readings of the same meter, rolling the register over where the
    /// dials have wrapped.
    /// </summary>
    /// <param name="previous">The earlier reading.</param>
    /// <param name="current">The later reading.</param>
    /// <param name="registerDigits">How many whole digits that meter's register carries.</param>
    /// <exception cref="MeterValidationException">
    /// A reading is negative, or is larger than a register that wide can display, or the width
    /// itself is not one GridCore stores.
    /// </exception>
    public static ConsumptionResult Between(decimal previous, decimal current, int registerDigits)
    {
        var capacity = CapacityOf(registerDigits);

        RequireDisplayable(previous, capacity, nameof(previous), registerDigits);
        RequireDisplayable(current, capacity, nameof(current), registerDigits);

        // The ordinary case, and equal readings with it: a meter that has not moved has consumed
        // nothing, which is a zero-usage exception rather than a failure of this arithmetic.
        if (current >= previous)
        {
            return new ConsumptionResult(current - previous, RolledOver: false);
        }

        return new ConsumptionResult(capacity - previous + current, RolledOver: true);
    }

    private static void RequireWidth(int registerDigits)
    {
        if (registerDigits is < MinRegisterDigits or > MaxRegisterDigits)
        {
            throw new MeterValidationException(
                $"A meter register carries between {MinRegisterDigits} and {MaxRegisterDigits} whole digits; '{registerDigits}' is not one of them.");
        }
    }

    private static void RequireDisplayable(decimal reading, decimal capacity, string field, int registerDigits)
    {
        if (reading < 0m)
        {
            throw new MeterValidationException($"A meter reading cannot be negative; '{field}' is {reading}.");
        }

        if (reading >= capacity)
        {
            // Refused rather than wrapped for the caller. A reading a register cannot display is a
            // number somebody mistyped, and quietly folding it into range would turn one bad keystroke
            // into a plausible-looking bill.
            throw new MeterValidationException(
                $"A {registerDigits}-digit register cannot read {reading}; it counts up to {capacity - 0.001m}.");
        }
    }
}

/// <summary>
/// Whether a reading is one somebody should look at before it becomes a bill.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="ConsumptionCalculator"/> on purpose. The arithmetic is certain; this is
/// judgement, and the two have different reasons to change — a utility retunes what counts as high
/// usage far more often than it changes what subtraction means.
/// </para>
/// <para>
/// Everything here is measured <b>per day</b>, never per reading. Reading periods are not equal: a
/// cycle slips, a meter is read late, a premise is read twice in a fortnight after a dispute. Left
/// per period, a two-month gap would raise a high-usage exception on a household that used exactly
/// what it always does.
/// </para>
/// </remarks>
public static class ReadingAssessment
{
    /// <summary>
    /// How many times its own typical daily usage a premise may consume before the reading is held
    /// for somebody to look at. Three is deliberately loose: this queue is worked by hand, and an
    /// exception nobody has time to clear is one nobody reads.
    /// </summary>
    public const decimal HighUsageMultiple = 3m;

    /// <summary>
    /// Classifies a reading against what the premise normally uses.
    /// </summary>
    /// <param name="reading">What the dials read, or <see langword="null"/> if the meter could not be read.</param>
    /// <param name="consumption">
    /// Units since the previous reading, or <see langword="null"/> when there is nothing to measure
    /// from — the first reading on a meter fitted without one.
    /// </param>
    /// <param name="days">Days the consumption covers. Clamped to at least one.</param>
    /// <param name="typicalDailyConsumption">
    /// What this premise normally uses in a day, or <see langword="null"/> when it has no history to
    /// judge by.
    /// </param>
    public static ReadingExceptionCode Classify(
        decimal? reading,
        decimal? consumption,
        int days,
        decimal? typicalDailyConsumption)
    {
        // No dials read at all. Checked first: a missing read has no consumption either, and
        // reporting it as "nothing to compare" would lose the fact that nobody got to the meter.
        if (reading is null)
        {
            return ReadingExceptionCode.MissingRead;
        }

        // A first reading establishes the baseline and measures nothing. Not an exception: there is
        // no anomaly in a meter that has only been read once.
        if (consumption is not { } used)
        {
            return ReadingExceptionCode.None;
        }

        // Dials that have not moved. Flagged whether or not the premise has any history, because
        // the two explanations — an empty property and a stopped meter — are both worth a look, and
        // only one of them is safe to bill.
        if (used == 0m)
        {
            return ReadingExceptionCode.ZeroUsage;
        }

        if (typicalDailyConsumption is not { } typical || typical <= 0m)
        {
            // Nothing to judge against. A newly metered premise gets no high-usage exception rather
            // than one built on a baseline of nothing.
            return ReadingExceptionCode.None;
        }

        var daily = used / Math.Max(days, 1);

        return daily > typical * HighUsageMultiple
            ? ReadingExceptionCode.HighUsage
            : ReadingExceptionCode.None;
    }

    /// <summary>
    /// Whole days between two reading dates, never fewer than one. Two readings on the same day
    /// count as a day: dividing by zero is worse than treating a same-day re-read as a short period.
    /// </summary>
    public static int DaysBetween(DateTimeOffset from, DateTimeOffset to) =>
        Math.Max(1, (int)Math.Round((to - from).TotalDays, MidpointRounding.AwayFromZero));
}
