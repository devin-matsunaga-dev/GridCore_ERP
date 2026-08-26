using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Features.Delinquency;

namespace GridCore.Modules.Billing.UnitTests.Delinquency;

/// <summary>
/// The debtors' ageing (WP-2.19): what counts as past due, how late it is, and which band it falls
/// in. Pure — no database anywhere near it, which is the whole reason the boundary cases below are
/// cheap enough to state one at a time.
/// </summary>
public class ArrearsAgeingTests
{
    private static readonly Guid Account = Guid.CreateVersion7();
    private static readonly DateOnly AsOf = new(2026, 9, 1);

    private static ArrearsBill Bill(string number, DateOnly? due, decimal balance) =>
        ArrearsAgeing.Line(Guid.CreateVersion7(), number, due, balance, AsOf);

    private static AccountArrears Compose(params ArrearsBill[] bills) =>
        ArrearsAgeing.Compose(Account, "USD", AsOf, bills);

    [Fact]
    public void A_bill_due_today_is_not_late()
    {
        // The customer has the whole of the due day. This is also the predicate
        // BillService.ReviewOverdueAsync moves a bill to Overdue on, and an ageing that disagreed
        // with the register's own status would make "overdue but not in arrears" a real question.
        var bill = Bill("BIL-000001", AsOf, 100.00m);

        Assert.False(bill.IsPastDue);
        Assert.Equal(0, bill.DaysPastDue);
    }

    [Fact]
    public void A_bill_due_yesterday_is_one_day_late()
    {
        var bill = Bill("BIL-000001", AsOf.AddDays(-1), 100.00m);

        Assert.True(bill.IsPastDue);
        Assert.Equal(1, bill.DaysPastDue);
    }

    [Fact]
    public void A_bill_not_yet_due_is_nought_days_past_due_and_never_a_negative_number()
    {
        // "Minus nine days overdue" is not a thing a rep says, and a negative here would sum into a
        // band that means nothing.
        var bill = Bill("BIL-000001", AsOf.AddDays(9), 100.00m);

        Assert.Equal(0, bill.DaysPastDue);
    }

    [Fact]
    public void A_bill_with_no_due_date_is_never_late()
    {
        // A draft, which the query never returns — the guard is here so that a nullable column
        // meaning "not yet asked for" cannot silently become "overdue since the epoch".
        var bill = Bill("BIL-000001", due: null, 100.00m);

        Assert.False(bill.IsPastDue);
        Assert.Equal(0, bill.DaysPastDue);
    }

    [Fact]
    public void Past_due_is_not_outstanding_and_the_whole_package_turns_on_the_difference()
    {
        // A bill issued on the 28th and due next month is money the utility is owed and is NOT money
        // the customer is late with. The 1% is taken on the second figure.
        var arrears = Compose(
            Bill("BIL-000001", AsOf.AddDays(-40), 120.00m),
            Bill("BIL-000002", AsOf.AddDays(14), 95.50m));

        Assert.Equal(215.50m, arrears.OutstandingAmount);
        Assert.Equal(120.00m, arrears.PastDueAmount);
        Assert.Equal(95.50m, arrears.CurrentAmount);
        Assert.True(arrears.IsInArrears);
    }

    [Fact]
    public void An_account_with_nothing_late_is_not_in_arrears()
    {
        var arrears = Compose(Bill("BIL-000001", AsOf.AddDays(14), 95.50m));

        Assert.False(arrears.IsInArrears);
        Assert.Equal(0m, arrears.PastDueAmount);
        Assert.Null(arrears.OldestDueDate);
        Assert.Equal(0, arrears.DaysPastDue);
    }

    [Fact]
    public void The_days_past_due_reported_is_the_OLDEST_debts_and_not_the_newest()
    {
        // A customer who paid last month's bill and not the one before is as delinquent as the older
        // debt says. The dunning steps and the statutory clock are both measured from this figure.
        var arrears = Compose(
            Bill("BIL-000002", AsOf.AddDays(-5), 60.00m),
            Bill("BIL-000001", AsOf.AddDays(-70), 40.00m));

        Assert.Equal(70, arrears.DaysPastDue);
        Assert.Equal(AsOf.AddDays(-70), arrears.OldestDueDate);
    }

    [Fact]
    public void Every_past_due_bill_lands_in_exactly_one_band_and_the_bands_add_up_to_the_total()
    {
        var arrears = Compose(
            Bill("BIL-000001", AsOf.AddDays(-1), 10.00m),
            Bill("BIL-000002", AsOf.AddDays(-30), 20.00m),
            Bill("BIL-000003", AsOf.AddDays(-31), 40.00m),
            Bill("BIL-000004", AsOf.AddDays(-61), 80.00m),
            Bill("BIL-000005", AsOf.AddDays(-91), 160.00m),
            Bill("BIL-000006", AsOf.AddDays(7), 5.00m));

        var bands = arrears.Buckets.ToDictionary(bucket => bucket.Label, bucket => bucket.Amount, StringComparer.Ordinal);

        Assert.Equal(5.00m, bands["Not yet due"]);
        Assert.Equal(30.00m, bands["1-30 days"]);
        Assert.Equal(40.00m, bands["31-60 days"]);
        Assert.Equal(80.00m, bands["61-90 days"]);
        Assert.Equal(160.00m, bands["Over 90 days"]);

        // The ageing reconciles to the register it was built from — the property a debtors' report
        // is worthless without.
        Assert.Equal(arrears.OutstandingAmount, arrears.Buckets.Sum(bucket => bucket.Amount));
    }

    [Fact]
    public void The_oldest_band_is_open_ended_so_nothing_ages_out_of_the_report()
    {
        var arrears = Compose(Bill("BIL-000001", AsOf.AddDays(-3650), 500.00m));

        Assert.Equal(500.00m, Assert.Single(arrears.Buckets, bucket => bucket.Label == "Over 90 days").Amount);
        Assert.Null(ArrearsAgeing.Bands[^1].ToDays);
    }

    [Fact]
    public void A_bill_settled_to_the_cent_is_dropped_rather_than_aged_at_nothing()
    {
        // It is still PartiallyPaid until the register moves it, and carrying it would put a zero row
        // in an ageing and a bill number in front of a rep who has nothing to say about it.
        var arrears = Compose(
            Bill("BIL-000001", AsOf.AddDays(-40), 0m),
            Bill("BIL-000002", AsOf.AddDays(-40), 25.00m));

        var bill = Assert.Single(arrears.Bills);

        Assert.Equal("BIL-000002", bill.BillNumber);
        Assert.Equal(25.00m, arrears.PastDueAmount);
    }

    [Fact]
    public void The_bills_come_back_oldest_due_date_first_which_is_the_order_a_deposit_is_applied_in()
    {
        var arrears = Compose(
            Bill("BIL-000003", AsOf.AddDays(-5), 30.00m),
            Bill("BIL-000001", AsOf.AddDays(-90), 10.00m),
            Bill("BIL-000002", AsOf.AddDays(-45), 20.00m));

        Assert.Equal(["BIL-000001", "BIL-000002", "BIL-000003"], arrears.Bills.Select(bill => bill.BillNumber));
    }

    [Fact]
    public void An_account_with_no_outstanding_bills_answers_with_an_empty_picture_rather_than_nothing()
    {
        var arrears = Compose();

        Assert.Empty(arrears.Bills);
        Assert.Equal(0m, arrears.OutstandingAmount);
        Assert.False(arrears.IsInArrears);

        // The bands are still there, all at nothing: a screen renders an ageing whether or not the
        // customer owes anything, and a caller should never have to cope with a missing row.
        Assert.Equal(ArrearsAgeing.Bands.Count, arrears.Buckets.Count);
    }
}
