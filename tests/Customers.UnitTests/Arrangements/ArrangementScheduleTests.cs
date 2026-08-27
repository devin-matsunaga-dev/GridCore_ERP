using GridCore.Modules.Customers.Features.Arrangements;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.UnitTests.Arrangements;

/// <summary>
/// The arithmetic behind an arrangement (WP-2.20). Pure: no database, no customer, no bill — a
/// schedule is a function of four numbers and two dates, which is why WORK_PACKAGES.md's first
/// verify item is a fast test.
/// </summary>
public sealed class ArrangementScheduleTests
{
    private static readonly DateOnly Made = new(2026, 8, 27);
    private static readonly DateOnly First = new(2026, 9, 26);

    private static IReadOnlyList<ScheduledInstalment> Build(
        decimal balance,
        decimal downPayment = 0m,
        int instalmentCount = 3,
        int intervalDays = ArrangementSchedule.DefaultIntervalDays) =>
        ArrangementSchedule.Build(balance, downPayment, instalmentCount, Made, First, intervalDays);

    [Theory]
    [InlineData(100.00, 3)]
    [InlineData(100.00, 6)]
    [InlineData(0.05, 2)]
    [InlineData(1234.57, 7)]
    [InlineData(999.99, 12)]
    [InlineData(50.00, 1)]
    public void The_instalments_sum_to_the_balance_exactly(decimal balance, int instalmentCount)
    {
        // THE INVARIANT THE WHOLE FEATURE RESTS ON. A schedule that did not add up would have a
        // customer keeping every instalment and still owing money.
        var schedule = Build(balance, instalmentCount: instalmentCount);

        Assert.Equal(balance, Money.Total(schedule.Select(line => line.Amount)));
    }

    [Fact]
    public void The_remainder_lands_on_the_last_instalment_rather_than_being_spread()
    {
        // WORK_PACKAGES.md's own wording. $100 over three is 33.33, 33.33, 33.34 — a column a
        // customer can check down the telephone — and never 33.34, 33.33, 33.33 or three figures
        // that each carry a third of a cent.
        var schedule = Build(100.00m, instalmentCount: 3);

        Assert.Equal([33.33m, 33.33m, 33.34m], schedule.Select(line => line.Amount));
    }

    [Fact]
    public void An_evenly_divisible_balance_has_no_remainder_to_land()
    {
        var schedule = Build(120.00m, instalmentCount: 4);

        Assert.All(schedule, line => Assert.Equal(30.00m, line.Amount));
    }

    [Fact]
    public void The_down_payment_is_the_first_line_and_is_due_the_day_the_arrangement_is_made()
    {
        // A line of the schedule rather than a deduction from it: it is what the customer actually
        // pays, and putting it in the schedule keeps "the lines add up to the balance" true of the
        // whole promise rather than of the part after the deposit.
        var schedule = Build(500.00m, downPayment: 100.00m, instalmentCount: 4);

        var down = schedule[0];

        Assert.True(down.IsDownPayment);
        Assert.Equal(1, down.Sequence);
        Assert.Equal(100.00m, down.Amount);
        Assert.Equal(Made, down.DueDate);

        Assert.Equal(500.00m, Money.Total(schedule.Select(line => line.Amount)));
        Assert.All(schedule.Skip(1), line => Assert.Equal(100.00m, line.Amount));
    }

    [Fact]
    public void With_no_down_payment_the_schedule_is_the_instalments_alone()
    {
        var schedule = Build(300.00m, instalmentCount: 3);

        Assert.DoesNotContain(schedule, line => line.IsDownPayment);
        Assert.Equal([1, 2, 3], schedule.Select(line => line.Sequence));
    }

    [Fact]
    public void The_instalments_fall_at_the_stated_interval_from_the_first()
    {
        var schedule = Build(300.00m, instalmentCount: 3, intervalDays: 14);

        Assert.Equal(
            [First, First.AddDays(14), First.AddDays(28)],
            schedule.Select(line => line.DueDate));
    }

    [Fact]
    public void A_balance_of_nothing_is_refused() =>
        // FAILURE PATH. An arrangement promising nothing is not a promise, and it would put an
        // account under protection for free.
        Assert.Throws<RegistryValidationException>(() => Build(0m));

    [Fact]
    public void A_balance_finer_than_a_cent_is_refused_rather_than_rounded() =>
        // The rule Money states: rounding is for figures GridCore computes, refusal is for figures
        // somebody typed.
        Assert.Throws<RegistryValidationException>(() => Build(100.005m));

    [Fact]
    public void A_down_payment_covering_the_whole_balance_is_refused()
    {
        // It is a payment, and dressing one up as an arrangement would put an account under the
        // protection of a promise with nothing left to promise.
        var failure = Assert.Throws<RegistryValidationException>(() => Build(200.00m, downPayment: 200.00m));

        Assert.Contains("Take the payment instead", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_down_payment_larger_than_the_balance_is_refused() =>
        Assert.Throws<RegistryValidationException>(() => Build(200.00m, downPayment: 250.00m));

    [Fact]
    public void A_negative_down_payment_is_refused() =>
        Assert.Throws<RegistryValidationException>(() => Build(200.00m, downPayment: -10.00m));

    [Fact]
    public void A_schedule_of_no_instalments_is_refused() =>
        Assert.Throws<RegistryValidationException>(() => Build(200.00m, instalmentCount: 0));

    [Fact]
    public void More_instalments_than_GridCore_will_schedule_at_all_is_refused()
    {
        var failure = Assert.Throws<RegistryValidationException>(() =>
            Build(5_000.00m, instalmentCount: PaymentArrangement.MaximumInstalments + 1));

        Assert.Contains($"{PaymentArrangement.MaximumInstalments}", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interval_longer_than_a_quarter_is_refused() =>
        // Six instalments at 365-day intervals would be a six-year debt inside a residential rep's
        // authority, which is what makes the instalment ceiling meaningless without this.
        Assert.Throws<RegistryValidationException>(() =>
            Build(600.00m, instalmentCount: 6, intervalDays: ArrangementSchedule.MaximumIntervalDays + 1));

    [Fact]
    public void A_first_instalment_falling_before_the_arrangement_is_refused() =>
        Assert.Throws<RegistryValidationException>(() =>
            ArrangementSchedule.Build(300.00m, 0m, 3, Made, Made.AddDays(-1)));

    [Fact]
    public void A_spread_that_would_leave_an_instalment_at_nothing_is_refused()
    {
        // Two cents over three instalments would schedule 0.00, 0.00 and 0.02 — and a due date with
        // nothing due on it is a date a customer can miss.
        var failure = Assert.Throws<RegistryValidationException>(() => Build(0.02m, instalmentCount: 3));

        Assert.Contains("Spread it over fewer", failure.Message, StringComparison.Ordinal);
    }
}
