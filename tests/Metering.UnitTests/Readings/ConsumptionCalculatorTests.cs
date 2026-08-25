using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Metering.Features.Shared;

namespace GridCore.Modules.Metering.UnitTests.Readings;

/// <summary>
/// The arithmetic every bill in GridCore is ultimately built on, exercised exhaustively because it
/// is pure and costs microseconds — CONVENTIONS.md's ⚡ rule that anything testable without
/// infrastructure must be.
/// </summary>
public sealed class ConsumptionCalculatorTests
{
    [Theory]
    [InlineData(4, 10_000)]
    [InlineData(5, 100_000)]
    [InlineData(6, 1_000_000)]
    [InlineData(9, 1_000_000_000)]
    public void A_register_counts_up_to_ten_to_the_power_of_its_digits(int digits, decimal capacity) =>
        Assert.Equal(capacity, ConsumptionCalculator.CapacityOf(digits));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(-1)]
    public void A_register_width_GridCore_does_not_store_is_refused(int digits) =>
        // Failure path. A width outside this range is not a meter: below four the dials would wrap
        // within a year, and above nine the arithmetic outgrows what the column holds.
        Assert.Throws<MeterValidationException>(() => ConsumptionCalculator.CapacityOf(digits));

    [Fact]
    public void Consumption_is_the_difference_between_two_readings()
    {
        var used = ConsumptionCalculator.Between(14_820.500m, 15_120.750m, 5);

        Assert.Equal(300.250m, used.Consumption);
        Assert.False(used.RolledOver);
    }

    [Fact]
    public void A_meter_that_has_not_moved_consumed_nothing()
    {
        // Not an error and not a rollover: an empty property, or a stopped meter. Which of the two
        // it is, is ReadingAssessment's question.
        var used = ConsumptionCalculator.Between(22_101.000m, 22_101.000m, 5);

        Assert.Equal(0m, used.Consumption);
        Assert.False(used.RolledOver);
    }

    [Fact]
    public void A_register_that_has_wrapped_is_measured_round_the_top_and_not_backwards()
    {
        // The case this whole class exists for. Read plainly, 99 850 → 120 is minus 99 730 units —
        // a credit for a register's worth of energy the customer never got.
        var used = ConsumptionCalculator.Between(99_850m, 120m, 5);

        Assert.Equal(270m, used.Consumption);
        Assert.True(used.RolledOver);
    }

    [Fact]
    public void A_register_that_wrapped_to_exactly_zero_consumed_the_last_unit()
    {
        var used = ConsumptionCalculator.Between(99_999m, 0m, 5);

        Assert.Equal(1m, used.Consumption);
        Assert.True(used.RolledOver);
    }

    [Fact]
    public void The_same_pair_of_readings_means_different_things_on_different_registers()
    {
        // Why the register width is a column on the meter rather than a constant: these are the same
        // two numbers, and one answer is nearly ten times the other.
        Assert.Equal(270m, ConsumptionCalculator.Between(99_850m, 120m, 5).Consumption);
        Assert.Equal(900_270m, ConsumptionCalculator.Between(99_850m, 120m, 6).Consumption);
    }

    [Fact]
    public void A_rollover_keeps_its_three_decimal_places()
    {
        var used = ConsumptionCalculator.Between(99_999.750m, 0.250m, 5);

        Assert.Equal(0.500m, used.Consumption);
        Assert.True(used.RolledOver);
    }

    [Fact]
    public void Consumption_stays_exact_where_a_double_would_drift()
    {
        // decimal, never double. 0.1 + 0.2 is where a float answer starts being 0.30000000000000004,
        // and this number ends up on a bill.
        var used = ConsumptionCalculator.Between(0.100m, 0.300m, 5);

        Assert.Equal(0.200m, used.Consumption);
    }

    [Fact]
    public void A_reading_from_a_new_register_counts_from_zero() =>
        Assert.Equal(540m, ConsumptionCalculator.Between(0m, 540m, 5).Consumption);

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.001)]
    public void A_negative_reading_is_refused(double reading) =>
        Assert.Throws<MeterValidationException>(() => ConsumptionCalculator.Between(0m, (decimal)reading, 5));

    [Fact]
    public void A_reading_the_register_cannot_display_is_refused_rather_than_folded_into_range()
    {
        // Failure path, and deliberately not "wrapped for the caller": 100 000 on a five-digit meter
        // is a keystroke, and quietly reading it as 0 would turn one typo into a plausible bill.
        var refused = Assert.Throws<MeterValidationException>(() => ConsumptionCalculator.Between(0m, 100_000m, 5));

        Assert.Contains("99999.999", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_previous_reading_the_register_cannot_display_is_refused_too() =>
        Assert.Throws<MeterValidationException>(() => ConsumptionCalculator.Between(100_000m, 10m, 5));

    [Theory]
    [InlineData(0, 5, true)]
    [InlineData(99_999.999, 5, true)]
    [InlineData(100_000, 5, false)]
    [InlineData(-0.001, 5, false)]
    [InlineData(100_000, 6, true)]
    public void Fits_answers_whether_a_register_that_wide_could_show_the_number(double reading, int digits, bool fits) =>
        Assert.Equal(fits, ConsumptionCalculator.Fits((decimal)reading, digits));

    [Fact]
    public void Consumption_is_never_negative_whichever_way_the_dials_went()
    {
        // The property that matters: whatever pair of displayable readings arrives, the answer is a
        // quantity of energy. A negative one would be a credit note nobody authorised.
        var readings = new decimal[] { 0m, 0.001m, 1m, 500.250m, 50_000m, 99_999.999m };

        foreach (var previous in readings)
        {
            foreach (var current in readings)
            {
                Assert.True(ConsumptionCalculator.Between(previous, current, 5).Consumption >= 0m);
            }
        }
    }

    [Fact]
    public void A_rollover_and_the_reading_after_it_add_up_to_one_full_register()
    {
        // Round-trip: what was consumed to fill the register plus what was consumed after the wrap
        // is exactly the register's capacity. Holds the two halves of the branch together.
        const decimal Previous = 99_850m;
        const decimal Current = 120m;

        var over = ConsumptionCalculator.Between(Previous, Current, 5).Consumption;
        var under = ConsumptionCalculator.Between(Current, Previous, 5).Consumption;

        Assert.Equal(ConsumptionCalculator.CapacityOf(5), over + under);
    }
}
