using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Rating;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Billing.UnitTests.Bills;

/// <summary>
/// The bill aggregate: what it means to raise, send, part-pay, chase and withdraw one. Pure — no
/// database — because every rule here is the module's own business rather than a query.
/// </summary>
public class BillTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly PeriodStart = new(2026, 7, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 7, 31);
    private static readonly RegistryActor Actor = new("auth0|officer", "A billing officer");

    private static ServiceAccountSummary Account { get; } = new(
        Guid.CreateVersion7(),
        "A-000001",
        Guid.CreateVersion7(),
        "Ana Reyes",
        Guid.CreateVersion7(),
        "Active",
        HoldsPremise: true,
        DateTimeOffset.UnixEpoch);

    private static BilledReading Reading { get; } = new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "MTR-000001",
        1_000m,
        1_750m);

    private static RatePlan Plan => DefaultRatePlans.DefaultOn(PeriodEnd);

    private static RateCalculation Calculation(decimal consumption = 750m) =>
        RateEngine.Calculate(Plan, DefaultRatePlans.TiersOf(Plan), consumption);

    private static Bill Raise(decimal consumption = 750m, string? cycleCode = "2026-07") =>
        Bill.Calculate("BIL-000001", Account, Reading, Calculation(consumption), PeriodStart, PeriodEnd, Actor, Now, cycleCode);

    private static Bill Issued(decimal consumption = 750m)
    {
        var bill = Raise(consumption);

        bill.Issue(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 24), Actor, Now);

        return bill;
    }

    [Fact]
    public void A_new_bill_is_a_draft_and_owes_nothing_yet()
    {
        var bill = Raise();

        Assert.Equal(BillStatus.Draft, bill.Status);
        Assert.False(bill.IsOutstanding);
        Assert.Null(bill.IssuedOn);
        Assert.Null(bill.DueDate);
        Assert.Equal(Money.Zero, bill.AmountPaid);
        Assert.Equal(bill.TotalAmount, bill.Balance);
    }

    [Fact]
    public void A_bill_equals_the_sum_of_its_own_lines()
    {
        // THE MONEY GUARD. Invariant 3 makes Finance assert that a posting balances; the equivalent
        // here is that a bill equals the sum of what is printed on it.
        var bill = Raise();

        Assert.Equal(bill.TotalAmount, Money.Total(bill.Lines.Select(line => line.Amount)));
        Assert.True(Money.IsRounded(bill.TotalAmount));
    }

    [Fact]
    public void A_calculation_that_does_not_add_up_is_refused()
    {
        // Failure path, and refused rather than corrected: a total silently replaced by the sum of
        // the lines would hide whatever produced the disagreement, and the next bill would carry it.
        var honest = Calculation();
        var tampered = honest with { Total = honest.Total + 1m };

        var exception = Assert.Throws<BillingValidationException>(() =>
            Bill.Calculate("BIL-000001", Account, Reading, tampered, PeriodStart, PeriodEnd, Actor, Now));

        Assert.Contains("must equal the sum of what is printed on it", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_total_finer_than_a_cent_is_refused()
    {
        var honest = Calculation();

        var tampered = honest with
        {
            Charges = [.. honest.Charges.Select(charge => charge with { Amount = charge.Amount + 0.001m })],
        };

        var balanced = tampered with { Total = Money.Total(tampered.Charges.Select(charge => charge.Amount)) };

        Assert.Throws<BillingValidationException>(() =>
            Bill.Calculate("BIL-000001", Account, Reading, balanced, PeriodStart, PeriodEnd, Actor, Now));
    }

    [Fact]
    public void A_bill_stamps_everything_it_needs_to_be_read_on_its_own()
    {
        // Not denormalisation for speed. Every one of these belongs to another module that is free
        // to change it, and resolving them at read time would give a customer a different bill on a
        // second look.
        var bill = Raise();

        Assert.Equal(Account.AccountNumber, bill.AccountNumber);
        Assert.Equal(Account.CustomerName, bill.CustomerName);
        Assert.Equal(Account.ServiceLocationId, bill.ServiceLocationId);
        Assert.Equal(Reading.MeterNumber, bill.MeterNumber);
        Assert.Equal(Reading.PreviousReading, bill.PreviousReading);
        Assert.Equal(Reading.CurrentReading, bill.CurrentReading);
        Assert.Equal(Plan.Code, bill.RatePlanCode);
        Assert.Equal(Plan.EffectiveFrom, bill.RatePlanEffectiveFrom);
        Assert.Equal(Plan.Currency, bill.Currency);
        Assert.Equal("A billing officer", bill.ActorName);
    }

    [Fact]
    public void A_period_that_ends_before_it_starts_is_refused()
    {
        var exception = Assert.Throws<BillingValidationException>(() =>
            Bill.Calculate("BIL-000001", Account, Reading, Calculation(), PeriodEnd, PeriodStart, Actor, Now));

        Assert.Contains("cannot end before it starts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bill_with_no_number_is_refused() =>
        Assert.Throws<BillingValidationException>(() =>
            Bill.Calculate("  ", Account, Reading, Calculation(), PeriodStart, PeriodEnd, Actor, Now));

    [Fact]
    public void Issuing_makes_it_money_the_utility_is_owed()
    {
        var bill = Issued();

        Assert.Equal(BillStatus.Issued, bill.Status);
        Assert.True(bill.IsOutstanding);
        Assert.Equal(new DateOnly(2026, 8, 3), bill.IssuedOn);
        Assert.Equal(new DateOnly(2026, 8, 24), bill.DueDate);
    }

    [Fact]
    public void Issuing_a_bill_twice_is_refused()
    {
        // Failure path: a 409 from the aggregate, because legality depends on where the bill is now
        // — which no validator at the edge can see. Finance would otherwise post the receivable
        // twice.
        var bill = Issued();

        var exception = Assert.Throws<BillingWorkflowException>(() =>
            bill.Issue(new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 25), Actor, Now));

        Assert.Contains("cannot move to Issued", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_due_date_before_the_issue_date_is_refused()
    {
        var bill = Raise();

        Assert.Throws<BillingValidationException>(() =>
            bill.Issue(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 2), Actor, Now));
    }

    [Fact]
    public void Part_of_the_money_leaves_the_bill_part_paid()
    {
        var bill = Issued();

        bill.RecordPayment(50m, Actor, Now);

        Assert.Equal(BillStatus.PartiallyPaid, bill.Status);
        Assert.Equal(50m, bill.AmountPaid);
        Assert.Equal(bill.TotalAmount - 50m, bill.Balance);
        Assert.Null(bill.PaidAt);
    }

    [Fact]
    public void A_second_instalment_is_recorded_against_a_bill_that_is_already_part_paid()
    {
        // PartiallyPaid → PartiallyPaid is deliberately absent from the state machine, so this is
        // the case that would throw if the aggregate consulted it blindly.
        var bill = Issued();

        bill.RecordPayment(20m, Actor, Now);
        bill.RecordPayment(30m, Actor, Now);

        Assert.Equal(BillStatus.PartiallyPaid, bill.Status);
        Assert.Equal(50m, bill.AmountPaid);
    }

    [Fact]
    public void Paying_the_balance_settles_the_bill()
    {
        var bill = Issued();

        bill.RecordPayment(bill.TotalAmount, Actor, Now);

        Assert.Equal(BillStatus.Paid, bill.Status);
        Assert.Equal(Money.Zero, bill.Balance);
        Assert.Equal(Now, bill.PaidAt);
        Assert.False(bill.IsOutstanding);
        Assert.Empty(bill.AllowedTransitions);
    }

    [Fact]
    public void Instalments_that_add_up_to_the_balance_settle_it()
    {
        var bill = Issued();
        var third = Money.Round(bill.TotalAmount / 3m);

        bill.RecordPayment(third, Actor, Now);
        bill.RecordPayment(third, Actor, Now);
        bill.RecordPayment(bill.Balance, Actor, Now);

        Assert.Equal(BillStatus.Paid, bill.Status);
        Assert.Equal(Money.Zero, bill.Balance);
    }

    [Fact]
    public void Paying_more_than_is_owed_is_refused()
    {
        // Failure path. Refused rather than absorbed: an overpayment is a credit on the account,
        // which is Finance's to hold — a bill that quietly swallowed it would leave money with no
        // record of where it went.
        var bill = Issued();

        var exception = Assert.Throws<BillingWorkflowException>(() =>
            bill.RecordPayment(bill.TotalAmount + 0.01m, Actor, Now));

        Assert.Contains("is more than is owed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(Money.Zero, bill.AmountPaid);
    }

    [Fact]
    public void Paying_a_bill_nobody_has_been_sent_is_refused()
    {
        var exception = Assert.Throws<BillingWorkflowException>(() => Raise().RecordPayment(10m, Actor, Now));

        Assert.Contains("is not owed", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_payment_that_is_not_positive_is_refused(double amount) =>
        Assert.Throws<BillingValidationException>(() => Issued().RecordPayment((decimal)amount, Actor, Now));

    [Fact]
    public void A_payment_finer_than_a_cent_is_refused() =>
        // Refused, not rounded: this is a figure a provider or a person stated, not one GridCore
        // computed. The same call WP-1.1 made for a deposit.
        Assert.Throws<BillingValidationException>(() => Issued().RecordPayment(10.001m, Actor, Now));

    [Fact]
    public void A_bill_past_its_due_date_and_still_owed_goes_overdue()
    {
        var bill = Issued();

        Assert.True(bill.MarkOverdue(new DateOnly(2026, 8, 25), Actor, Now));
        Assert.Equal(BillStatus.Overdue, bill.Status);
        Assert.True(bill.IsOutstanding);
    }

    [Fact]
    public void A_part_paid_bill_past_its_due_date_goes_overdue_too()
    {
        var bill = Issued();

        bill.RecordPayment(10m, Actor, Now);

        Assert.True(bill.MarkOverdue(new DateOnly(2026, 8, 25), Actor, Now));
        Assert.Equal(BillStatus.Overdue, bill.Status);
    }

    [Theory]

    // Not yet due, due today, already overdue, settled, withdrawn, never sent.
    [InlineData("2026-08-23")]
    [InlineData("2026-08-24")]
    public void A_bill_that_is_not_yet_past_due_does_not_move(string asOf)
    {
        var bill = Issued();

        Assert.False(bill.MarkOverdue(DateOnly.Parse(asOf, null), Actor, Now));
        Assert.Equal(BillStatus.Issued, bill.Status);
    }

    [Fact]
    public void An_overdue_review_leaves_alone_what_it_should()
    {
        // False rather than an exception: a review walks every outstanding bill, and "this one is
        // fine" is an ordinary answer.
        var late = DateOnly.Parse("2026-09-30", null);

        var draft = Raise();
        var paid = Issued();
        var cancelled = Issued();
        var already = Issued();

        paid.RecordPayment(paid.TotalAmount, Actor, Now);
        cancelled.Cancel("Billed in error.", Actor, Now);
        already.MarkOverdue(late, Actor, Now);

        Assert.False(draft.MarkOverdue(late, Actor, Now));
        Assert.False(paid.MarkOverdue(late, Actor, Now));
        Assert.False(cancelled.MarkOverdue(late, Actor, Now));
        Assert.False(already.MarkOverdue(late, Actor, Now));

        Assert.Equal(BillStatus.Draft, draft.Status);
        Assert.Equal(BillStatus.Paid, paid.Status);
        Assert.Equal(BillStatus.Cancelled, cancelled.Status);
        Assert.Equal(BillStatus.Overdue, already.Status);
    }

    [Fact]
    public void An_overdue_bill_is_still_paid_like_any_other()
    {
        var bill = Issued();

        bill.MarkOverdue(new DateOnly(2026, 9, 30), Actor, Now);
        bill.RecordPayment(bill.TotalAmount, Actor, Now);

        Assert.Equal(BillStatus.Paid, bill.Status);
    }

    [Fact]
    public void Cancelling_a_bill_needs_a_reason()
    {
        // Required, unlike most transitions' reasons: cancelling removes money the utility was owed,
        // and "why" is the first question asked of it.
        var exception = Assert.Throws<BillingValidationException>(() => Issued().Cancel("   ", Actor, Now));

        Assert.Contains("needs a reason", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelling_is_terminal()
    {
        var bill = Issued();

        bill.Cancel("Billed against a disputed reading.", Actor, Now);

        Assert.Equal(BillStatus.Cancelled, bill.Status);
        Assert.Equal("Billed against a disputed reading.", bill.StatusReason);
        Assert.Empty(bill.AllowedTransitions);
        Assert.False(bill.IsOutstanding);

        Assert.Throws<BillingWorkflowException>(() => bill.RecordPayment(1m, Actor, Now));
    }

    [Fact]
    public void A_settled_bill_cannot_be_cancelled()
    {
        var bill = Issued();

        bill.RecordPayment(bill.TotalAmount, Actor, Now);

        Assert.Throws<BillingWorkflowException>(() => bill.Cancel("Changed our minds.", Actor, Now));
    }

    [Fact]
    public void A_bill_with_no_consumption_still_carries_the_standing_charge()
    {
        // An empty house is billed; a premise that is not connected has no account to bill.
        var bill = Raise(consumption: 0m);

        Assert.Equal(Plan.MonthlyServiceCharge, bill.TotalAmount);
        Assert.Single(bill.Lines);
    }

    [Fact]
    public void The_lines_are_written_once_and_numbered_in_order()
    {
        var bill = Raise(1_500m);

        Assert.Equal([1, 2, 3, 4], bill.Lines.Select(line => line.Sequence));
        Assert.All(bill.Lines, line => Assert.Equal(bill.Id, line.BillId));
        Assert.Equal(ChargeKind.ServiceCharge, bill.Lines[0].Kind);
    }

    [Fact]
    public void An_ad_hoc_bill_carries_no_cycle_code() =>
        // What makes ux_bills_account_cycle work: NULLs are distinct, so an account can be billed by
        // hand as often as a correction needs while a cycle cannot be billed twice.
        Assert.Null(Raise(cycleCode: null).CycleCode);
}
