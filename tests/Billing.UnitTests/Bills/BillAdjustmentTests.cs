using GridCore.Contracts.Services;
using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Rating;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Billing.UnitTests.Bills;

/// <summary>
/// Correcting a bill: what may be corrected, by how much, what it leaves owing, and what it leaves
/// the document still saying. Pure — no database — because every rule here is the module's own
/// business rather than a query.
/// </summary>
/// <remarks>
/// The permission gate is the other half of invariant 5 and is asserted in
/// <see cref="BillEndpointsTests"/>, where the routing layer decides it; the audit entry and the
/// event are asserted in <see cref="BillServiceTests"/>, where the unit of work writes them.
/// </remarks>
public class BillAdjustmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddHours(1);
    private static readonly DateOnly PeriodStart = new(2026, 7, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 7, 31);
    private static readonly RegistryActor Actor = new("auth0|manager", "A billing manager");

    private static ServiceAccountSummary Account { get; } = new(
        Guid.CreateVersion7(),
        "A-000001",
        Guid.CreateVersion7(),
        "Ana Reyes",
        Guid.CreateVersion7(),
        "Active",
        ServiceType.Electricity,
        IsMetered: true,
        HoldsPremise: true,
        DateTimeOffset.UnixEpoch);

    private static BilledReading Reading { get; } = new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "MTR-000001",
        1_000m,
        1_750m);

    private static Bill Raise(decimal consumption = 750m)
    {
        var plan = DefaultRatePlans.DefaultOn(PeriodEnd);

        return Bill.Calculate(
            "BIL-000001",
            Account,
            Reading,
            RateEngine.Calculate(plan, DefaultRatePlans.TiersOf(plan), consumption),
            PeriodStart,
            PeriodEnd,
            Actor,
            Now,
            "2026-07");
    }

    private static Bill Issued(decimal consumption = 750m)
    {
        var bill = Raise(consumption);

        bill.Issue(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 24), Actor, Now);

        return bill;
    }

    [Fact]
    public void A_credit_reduces_what_is_owed_without_touching_what_was_printed()
    {
        // The whole point of the work package in one assertion. The customer holds a copy of the
        // bill; a correction that rewrote its total would leave the utility unable to reproduce it.
        var bill = Issued();
        var printed = bill.TotalAmount;

        bill.Adjust(BillAdjustmentKind.Credit, 20m, "Estimated read corrected.", Actor, Later);

        Assert.Equal(printed, bill.TotalAmount);
        Assert.Equal(-20m, bill.AdjustmentTotal);
        Assert.Equal(printed - 20m, bill.AmountDue);
        Assert.Equal(printed - 20m, bill.Balance);

        // And the lines still add up to the printed total, as they did before it was corrected.
        Assert.Equal(printed, Money.Total(bill.Lines.Select(line => line.Amount)));
    }

    [Fact]
    public void A_charge_increases_what_is_owed()
    {
        // A correction runs either way: a bill raised on a read that was too low leaves the customer
        // owing the difference, and re-raising the whole document would give them two.
        var bill = Issued();
        var printed = bill.TotalAmount;

        bill.Adjust(BillAdjustmentKind.Charge, 15.50m, "Under-billed; the second tier was missed.", Actor, Later);

        Assert.Equal(printed, bill.TotalAmount);
        Assert.Equal(15.50m, bill.AdjustmentTotal);
        Assert.Equal(printed + 15.50m, bill.AmountDue);
    }

    [Fact]
    public void An_adjustment_records_who_made_it_why_and_what_it_left_owing()
    {
        var bill = Issued();

        var adjustment = bill.Adjust(
            BillAdjustmentKind.Credit,
            20m,
            "  Estimated read corrected after the customer disputed it.  ",
            Actor,
            Later);

        Assert.Equal(bill.Id, adjustment.BillId);
        Assert.Equal(1, adjustment.Sequence);
        Assert.Equal(BillAdjustmentKind.Credit, adjustment.Kind);
        Assert.Equal(-20m, adjustment.Amount);
        Assert.Equal(bill.AmountDue, adjustment.AmountDueAfter);
        Assert.Equal("Estimated read corrected after the customer disputed it.", adjustment.Reason);
        Assert.Equal("auth0|manager", adjustment.ActorId);
        Assert.Equal("A billing manager", adjustment.ActorName);
        Assert.Equal(Later, adjustment.RecordedAt);
        Assert.Equal(7, adjustment.Id.Version);
    }

    [Fact]
    public void Adjustments_accumulate_in_the_order_they_were_applied()
    {
        // amount_due_after is only readable down the page if the sequence is the order they were
        // made in. Numbered explicitly rather than by id: two entries minted inside one millisecond
        // have no defined order by Guid v7, which STATUS.md has warned about since WP-0.5.
        var bill = Issued();
        var printed = bill.TotalAmount;

        bill.Adjust(BillAdjustmentKind.Credit, 20m, "Estimated read corrected.", Actor, Later);
        bill.Adjust(BillAdjustmentKind.Charge, 5m, "Re-read came back higher than the estimate.", Actor, Later);

        Assert.Equal([1, 2], bill.Adjustments.Select(adjustment => adjustment.Sequence));
        Assert.Equal([printed - 20m, printed - 15m], bill.Adjustments.Select(adjustment => adjustment.AmountDueAfter));
        Assert.Equal(-15m, bill.AdjustmentTotal);
        Assert.Equal(printed - 15m, bill.AmountDue);
    }

    [Fact]
    public void The_status_does_not_move_for_a_correction_that_leaves_money_owing()
    {
        // Adjustment is a financial correction, not a lifecycle state — there is deliberately no
        // Adjusted status. What changed is on the entry and in the audit trail, not on the machine.
        var bill = Issued();

        bill.MarkOverdue(new DateOnly(2026, 9, 30), Actor, Now);
        bill.Adjust(BillAdjustmentKind.Credit, 20m, "Estimated read corrected.", Actor, Later);

        Assert.Equal(BillStatus.Overdue, bill.Status);
        Assert.True(bill.IsOutstanding);
    }

    [Theory]
    [InlineData("Issued")]
    [InlineData("Overdue")]
    [InlineData("PartiallyPaid")]
    public void A_credit_that_clears_the_balance_settles_the_bill(string from)
    {
        // Not an Adjusted state sneaking in: the bill is genuinely no longer owed, and leaving it
        // Issued would park a zero-balance row on the AR worklist for good. The machine still
        // decides — all three of these may legally reach Paid.
        var bill = Issued();

        switch (from)
        {
            case "Overdue":
                bill.MarkOverdue(new DateOnly(2026, 9, 30), Actor, Now);

                break;

            case "PartiallyPaid":
                bill.RecordPayment(10m, Actor, Now);

                break;

            default:
                break;
        }

        bill.Adjust(BillAdjustmentKind.Credit, bill.Balance, "Billed against a read that was not this meter's.", Actor, Later);

        Assert.Equal(BillStatus.Paid, bill.Status);
        Assert.Equal(Money.Zero, bill.Balance);
        Assert.Equal(Later, bill.PaidAt);
        Assert.False(bill.IsOutstanding);

        // Settled by a credit is not the same as paid: no money came in, and the ledger must not
        // pretend otherwise.
        Assert.Equal(from is "PartiallyPaid" ? 10m : Money.Zero, bill.AmountPaid);
    }

    [Fact]
    public void A_credit_larger_than_the_balance_is_refused()
    {
        // Refused rather than absorbed. Crediting more than is owed leaves money on the account, and
        // a credit balance is Finance's to hold (WP-2.6) — a bill that quietly swallowed the
        // difference would leave it with no record of where it went.
        var bill = Issued();

        var exception = Assert.Throws<BillingWorkflowException>(() =>
            bill.Adjust(BillAdjustmentKind.Credit, bill.Balance + 0.01m, "Goodwill.", Actor, Later));

        Assert.Contains("more than is owed", exception.Message, StringComparison.Ordinal);

        // And nothing moved: the guards run before the first mutation (WP-1.4's ordering rule).
        Assert.Equal(Money.Zero, bill.AdjustmentTotal);
        Assert.Empty(bill.Adjustments);
        Assert.Equal(BillStatus.Issued, bill.Status);
    }

    [Fact]
    public void A_credit_larger_than_what_is_left_after_a_payment_is_refused()
    {
        // The balance, not the total: half of it has already been paid, and crediting the whole
        // amount would owe the customer money.
        var bill = Issued();

        bill.RecordPayment(30m, Actor, Now);

        Assert.Throws<BillingWorkflowException>(() =>
            bill.Adjust(BillAdjustmentKind.Credit, bill.TotalAmount, "Goodwill.", Actor, Later));
    }

    [Fact]
    public void A_draft_cannot_be_adjusted()
    {
        // Nobody has been asked for the money, so there is nothing to correct: a wrong draft is
        // re-run or thrown away.
        var exception = Assert.Throws<BillingWorkflowException>(() =>
            Raise().Adjust(BillAdjustmentKind.Credit, 5m, "Estimated read corrected.", Actor, Later));

        Assert.Contains("billing it again", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BillStatus.Paid)]
    [InlineData(BillStatus.Cancelled)]
    public void A_settled_or_withdrawn_bill_cannot_be_adjusted(BillStatus status)
    {
        // Money moving after a bill is settled is a refund, which is the Payments module's act and
        // Finance's entry — the same line RecordPayment draws.
        var bill = Issued();

        if (status is BillStatus.Paid)
        {
            bill.RecordPayment(bill.TotalAmount, Actor, Now);
        }
        else
        {
            bill.Cancel("Billed in error.", Actor, Now);
        }

        var exception = Assert.Throws<BillingWorkflowException>(() =>
            bill.Adjust(BillAdjustmentKind.Credit, 5m, "Goodwill.", Actor, Later));

        Assert.Contains("is a refund", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-20)]
    public void An_adjustment_must_be_a_positive_amount(decimal amount)
    {
        // The direction is the kind, not the sign. A negative credit is a caller saying "credit"
        // twice, and applying it would put money ON the bill.
        var exception = Assert.Throws<BillingValidationException>(() =>
            Issued().Adjust(BillAdjustmentKind.Credit, amount, "Estimated read corrected.", Actor, Later));

        Assert.Contains("must be a positive amount", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_adjustment_finer_than_a_cent_is_refused_not_rounded()
    {
        // A figure somebody typed, not one GridCore computed — Money is explicit about the
        // difference, and RecordPayment makes the same call about a payment.
        var exception = Assert.Throws<BillingValidationException>(() =>
            Issued().Adjust(BillAdjustmentKind.Credit, 20.005m, "Estimated read corrected.", Actor, Later));

        Assert.Contains("finer than that", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Adjusting_a_bill_needs_a_reason(string reason)
    {
        // Invariant 5: a sensitive action is permission-gated AND audited, and an audit entry that
        // does not say why is a row nobody can act on.
        var exception = Assert.Throws<BillingValidationException>(() =>
            Issued().Adjust(BillAdjustmentKind.Credit, 20m, reason, Actor, Later));

        Assert.Contains("needs a reason", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kind_the_module_does_not_know_is_refused_rather_than_guessed()
    {
        // Cast in from the wire past the validator. Guessing which way an unknown correction moves
        // money is the one thing worse than failing.
        var exception = Assert.Throws<BillingValidationException>(() =>
            Issued().Adjust((BillAdjustmentKind)99, 20m, "Estimated read corrected.", Actor, Later));

        Assert.Contains("not a kind of adjustment", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_adjustment_by_a_caller_with_no_subject_id_moves_nothing()
    {
        // The last guard that can fire, and the one it is easiest to fire late: the entry is built
        // before the running total moves, so a refusal here leaves the aggregate the caller is still
        // holding exactly as it was. WP-1.4 learned this from a stock adjustment that moved the
        // shelf and only then refused.
        var bill = Issued();

        Assert.Throws<BillingValidationException>(() =>
            bill.Adjust(BillAdjustmentKind.Credit, 20m, "Estimated read corrected.", new RegistryActor("  ", null), Later));

        Assert.Equal(Money.Zero, bill.AdjustmentTotal);
        Assert.Equal(bill.TotalAmount, bill.AmountDue);
        Assert.Empty(bill.Adjustments);
    }

    [Fact]
    public void An_adjustment_is_never_edited_or_removed()
    {
        // Append-only, as invariant 3 has the general ledger be. A second correction is another
        // entry; the first keeps saying what it said.
        var bill = Issued();

        var first = bill.Adjust(BillAdjustmentKind.Credit, 20m, "Estimated read corrected.", Actor, Later);

        bill.Adjust(BillAdjustmentKind.Charge, 5m, "Re-read came back higher.", Actor, Later);

        Assert.Equal(2, bill.Adjustments.Count);
        Assert.Same(first, bill.Adjustments[0]);
        Assert.Equal(-20m, first.Amount);
        Assert.Equal(bill.TotalAmount - 20m, first.AmountDueAfter);

        // The second entry states where the money ended up; the first still states where it was.
        Assert.Equal(bill.TotalAmount - 15m, bill.Adjustments[1].AmountDueAfter);
    }

    [Fact]
    public void A_corrected_bill_is_paid_off_at_what_it_now_comes_to()
    {
        // What ties the correction to the money: a credit means the customer pays less, and paying
        // the ORIGINAL total would now be an overpayment.
        var bill = Issued();

        bill.Adjust(BillAdjustmentKind.Credit, 20m, "Estimated read corrected.", Actor, Later);

        Assert.Throws<BillingWorkflowException>(() => bill.RecordPayment(bill.TotalAmount, Actor, Later));

        bill.RecordPayment(bill.AmountDue, Actor, Later);

        Assert.Equal(BillStatus.Paid, bill.Status);
        Assert.Equal(Money.Zero, bill.Balance);
    }
}
