using GridCore.Contracts.Events;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Fees;
using GridCore.Modules.Billing.Features.Rating;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Modules.Billing.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.UnitTests.Fees;

/// <summary>
/// Raising fees against a service account: what the schedule priced them at, what lands on a bill,
/// and who may do it. The billing schema and the platform schema share one SQLite connection here,
/// so a charge and its audit entry really do commit together.
/// </summary>
public class AccountChargeServiceTests
{
    private const string Cycle = "2026-08";

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReadAt = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private static BillingTestHost NewHost() =>
        new(new FakeClock(Now), new FakeCurrentUser("auth0|clerk", "A customer service rep"));

    private static Guid Premise() => Guid.CreateVersion7();

    private static RaiseChargeInput Reconnection(Guid accountId, DateOnly? on = null) =>
        new(accountId, FeeCode.Reconnection, "Supply restored after the arrears were settled.", on);

    [Fact]
    public async Task A_fee_is_raised_at_the_figure_the_schedule_publishes_today()
    {
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var charge = await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        // The repriced figure, because today is after the revision — see FeeSchedulesTests.
        Assert.Equal(60.00m, charge.Amount);
        Assert.Equal(FeeCode.Reconnection, charge.Code);
        Assert.Equal(AccountChargeStatus.Pending, charge.Status);
        Assert.Equal(account.AccountNumber, charge.AccountNumber);
        Assert.Null(charge.BillId);

        // Committed, not merely built.
        await using var context = host.NewBillingContext();

        Assert.Equal(1, await context.AccountCharges.CountAsync());
    }

    [Fact]
    public async Task A_charge_stamps_the_schedule_row_that_priced_it()
    {
        // THE PACKAGE'S CENTRAL CLAIM. Nothing downstream ever asks the catalogue again, which is
        // what lets a document reprinted after a repricing still show the figure the customer holds
        // a copy of — the shape DepositAssessment.RuleId already gives a deposit.
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var charge = await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        var row = FeeSchedules.InForceOn(FeeCode.Reconnection, Today)!;

        Assert.Equal(row.Id, charge.FeeScheduleId);
        Assert.Equal(row.EffectiveFrom, charge.ScheduleEffectiveFrom);
        Assert.Equal(row.Name, charge.Description);
    }

    [Fact]
    public async Task A_fee_raised_for_an_earlier_day_carries_that_days_figure_forever()
    {
        // A reconnection performed in June and charged in September is a June figure, and it stays
        // one: the charge holds the amount, not a pointer to a catalogue that has moved on.
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var charge = await host.WithChargesAsync(charges =>
            charges.RaiseAsync(Reconnection(account.Id, new DateOnly(2026, 6, 15))));

        Assert.Equal(50.00m, charge.Amount);
        Assert.Equal(FeeSchedules.OriginalEffectiveFrom, charge.ScheduleEffectiveFrom);

        await using var context = host.NewBillingContext();

        Assert.Equal(50.00m, (await context.AccountCharges.SingleAsync()).Amount);
    }

    [Fact]
    public async Task Raising_a_fee_is_audited_with_the_row_that_priced_it()
    {
        // INVARIANT 5. The snapshot carries the schedule row and the figure, which together are what
        // answer "why is this $60" after the schedule has moved on again.
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var charge = await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        await using var context = host.NewPlatformContext();

        var entry = await context.AuditEntries.SingleAsync(audit => audit.Action == AuditActions.AccountChargeRaised);

        Assert.Equal(AuditEntityTypes.AccountCharge, entry.EntityType);
        Assert.Equal(charge.Id.ToString(), entry.EntityId);
        Assert.Null(entry.BeforeJson);
        Assert.Contains(charge.FeeScheduleId.ToString(), entry.AfterJson!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Raising_a_fee_publishes_nothing()
    {
        // Deliberate, and the same call WP-2.15 made about a status change: nothing downstream
        // prices off a raised charge, and the receivable is raised by BillIssued when the fee
        // reaches a bill. An event nobody consumes is an instruction rather than a fact.
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        Assert.Empty(host.Events.Published);
    }

    [Fact]
    public async Task Charging_without_the_permission_is_refused()
    {
        // THE FAILURE PATH THIS WORK PACKAGE OWES, in the service rather than on the route: a caller
        // who may read the billing register but may not charge fees is refused, which the endpoint
        // renders as 403. The service demands it because WP-2.19 and WP-2.22 will call this in
        // process rather than over HTTP.
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var reader = new FakeCurrentUser("auth0|reader", "A billing clerk", new HashSet<string> { Permissions.Billing.Read });

        var refusal = await Assert.ThrowsAsync<BillingPermissionException>(() =>
            host.AsAsync(reader, charges => charges.RaiseAsync(Reconnection(account.Id))));

        Assert.Contains(Permissions.Billing.Charge, refusal.Message, StringComparison.Ordinal);

        // Nothing was written: the refusal happens inside the unit of work, before the row.
        await using var context = host.NewBillingContext();

        Assert.Equal(0, await context.AccountCharges.CountAsync());
    }

    [Fact]
    public async Task Charging_an_account_that_does_not_exist_is_refused()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<ServiceAccountNotFoundException>(() =>
            host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(Guid.CreateVersion7()))));
    }

    [Fact]
    public async Task A_fee_that_is_not_one_GridCore_declares_is_refused()
    {
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        await Assert.ThrowsAsync<BillingValidationException>(() =>
            host.WithChargesAsync(charges => charges.RaiseAsync(
                new RaiseChargeInput(account.Id, (FeeCode)987, "A fee nobody publishes."))));
    }

    [Fact]
    public async Task A_charge_raised_without_a_reason_is_refused()
    {
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        await Assert.ThrowsAsync<BillingValidationException>(() =>
            host.WithChargesAsync(charges => charges.RaiseAsync(
                new RaiseChargeInput(account.Id, FeeCode.Reconnection, "   "))));
    }

    [Fact]
    public async Task A_pending_charge_can_be_withdrawn_and_the_row_says_why()
    {
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var charge = await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        var withdrawn = await host.WithChargesAsync(charges =>
            charges.CancelAsync(charge.Id, new CancelChargeInput("Raised against the wrong account.")));

        Assert.Equal(AccountChargeStatus.Cancelled, withdrawn.Status);
        Assert.Equal("Raised against the wrong account.", withdrawn.StatusReason);
        Assert.True(AccountChargeTransitions.IsFinal(withdrawn.Status));

        await using var context = host.NewPlatformContext();

        Assert.Equal(
            1,
            await context.AuditEntries.CountAsync(audit => audit.Action == AuditActions.AccountChargeCancelled));
    }

    [Fact]
    public async Task A_charge_that_reached_a_bill_cannot_be_withdrawn()
    {
        // Billed is terminal: correcting a fee the customer has been sent is an adjustment to that
        // bill (WP-2.4), not a charge quietly moving out from under the document.
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var charge = await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        await host.WithChargesAsync(charges => charges.BillNowAsync(charge.Id, new BillChargeInput()));

        var refusal = await Assert.ThrowsAsync<BillingWorkflowException>(() =>
            host.WithChargesAsync(charges => charges.CancelAsync(charge.Id, new CancelChargeInput("Changed our mind."))));

        Assert.Contains("adjusting that bill", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Withdrawing_a_charge_that_does_not_exist_is_refused()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<AccountChargeNotFoundException>(() =>
            host.WithChargesAsync(charges =>
                charges.CancelAsync(Guid.CreateVersion7(), new CancelChargeInput("No such charge."))));
    }

    [Fact]
    public async Task Billing_a_charge_at_the_counter_raises_an_issued_bill_of_its_own()
    {
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var charge = await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        var counter = await host.WithChargesAsync(charges => charges.BillNowAsync(charge.Id, new BillChargeInput()));

        Assert.Equal(AccountChargeStatus.Billed, counter.Charge.Status);
        Assert.Equal(counter.Bill.Id, counter.Charge.BillId);
        Assert.Equal(counter.Bill.BillNumber, counter.Charge.BillNumber);

        // A charge bill: fees alone, no meter, no tariff, no period of supply — and issued in the
        // same act, because it exists so the customer can pay it now.
        Assert.Equal(BillKind.Charge, counter.Bill.Kind);
        Assert.Equal(BillStatus.Issued, counter.Bill.Status);
        Assert.Null(counter.Bill.MeterId);
        Assert.Null(counter.Bill.MeterNumber);
        Assert.Null(counter.Bill.RatePlanId);
        Assert.Null(counter.Bill.UnitOfMeasure);
        Assert.Equal(0m, counter.Bill.Consumption);
        Assert.Equal(Today, counter.Bill.PeriodStart);
        Assert.Equal(Today, counter.Bill.PeriodEnd);
        Assert.Null(counter.Bill.CycleCode);

        Assert.Equal(60.00m, counter.Bill.TotalAmount);
        Assert.Equal(60.00m, counter.Bill.FeeAmount);
    }

    [Fact]
    public async Task A_counter_bill_carries_one_fee_line_and_no_per_unit_fields()
    {
        // WHAT DISTINGUISHES A FEE LINE FROM A CONSUMPTION LINE, asserted where it is written: a
        // published figure carries no tier, no units and no rate.
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var charge = await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        var counter = await host.WithChargesAsync(charges => charges.BillNowAsync(charge.Id, new BillChargeInput()));

        var line = Assert.Single(counter.Bill.Lines);

        Assert.Equal(ChargeKind.Fee, line.Kind);
        Assert.Equal("Reconnection fee", line.Description);
        Assert.Equal(60.00m, line.Amount);
        Assert.Equal(1, line.Sequence);
        Assert.Null(line.TierSequence);
        Assert.Null(line.Units);
        Assert.Null(line.RatePerUnit);
    }

    [Fact]
    public async Task A_counter_bill_raises_the_receivable_and_says_how_much_of_it_is_fees()
    {
        // The event Finance posts from. On a charge bill the fee half IS the whole, which is what
        // makes it credit fee revenue and not utility revenue.
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var charge = await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        var counter = await host.WithChargesAsync(charges => charges.BillNowAsync(charge.Id, new BillChargeInput()));

        var issued = host.Events.Single<BillIssued>();

        Assert.Equal(counter.Bill.Id, issued.BillId);
        Assert.Equal(60.00m, issued.Amount);
        Assert.Equal(60.00m, issued.FeeAmount);
        Assert.Equal(account.Id, issued.ServiceAccountId);
    }

    [Fact]
    public async Task Billing_at_the_counter_audits_the_charge_and_the_bill_it_produced()
    {
        // Two entries, because two things happened. Leaving the bill's out would make "who issued
        // this bill" answerable for a cycle bill and not for a counter one.
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var charge = await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        await host.WithChargesAsync(charges => charges.BillNowAsync(charge.Id, new BillChargeInput()));

        await using var context = host.NewPlatformContext();

        var billed = await context.AuditEntries.SingleAsync(audit => audit.Action == AuditActions.AccountChargeBilled);

        Assert.Equal(AuditEntityTypes.AccountCharge, billed.EntityType);
        Assert.Contains(nameof(AccountChargeStatus.Pending), billed.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains(nameof(AccountChargeStatus.Billed), billed.AfterJson!, StringComparison.Ordinal);

        Assert.Equal(1, await context.AuditEntries.CountAsync(audit => audit.Action == AuditActions.BillIssued));
    }

    [Fact]
    public async Task A_charge_billed_at_the_counter_cannot_be_billed_again()
    {
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var charge = await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        await host.WithChargesAsync(charges => charges.BillNowAsync(charge.Id, new BillChargeInput()));

        await Assert.ThrowsAsync<BillingWorkflowException>(() =>
            host.WithChargesAsync(charges => charges.BillNowAsync(charge.Id, new BillChargeInput())));
    }

    [Fact]
    public async Task Billing_at_the_counter_without_the_permission_is_refused()
    {
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var charge = await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        var reader = new FakeCurrentUser("auth0|reader", "A billing clerk", new HashSet<string> { Permissions.Billing.Read });

        await Assert.ThrowsAsync<BillingPermissionException>(() =>
            host.AsAsync(reader, charges => charges.BillNowAsync(charge.Id, new BillChargeInput())));

        await using var context = host.NewBillingContext();

        Assert.Equal(0, await context.Bills.CountAsync());
    }

    [Fact]
    public async Task A_waiting_fee_lands_on_the_next_cycle_bill()
    {
        // "IT LANDS ON THE NEXT BILL", proved end to end in the fast tier: the fee is raised at the
        // desk, the cycle runs, and the bill carries it after the tariff's own lines.
        using var host = NewHost();

        var premise = Premise();
        var account = host.Accounts.Add(premise);

        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        var bill = Assert.Single(run.Bills);
        var fee = Assert.Single(bill.Lines, line => line.Kind is ChargeKind.Fee);

        Assert.Equal(60.00m, fee.Amount);
        Assert.Equal(60.00m, bill.FeeAmount);

        // Last on the document, after the standing charge and the consumption blocks.
        Assert.Equal(bill.Lines.Count, fee.Sequence);

        // The money guard holds with the fee on it: the bill equals the sum of what is printed.
        Assert.Equal(bill.TotalAmount, bill.Lines.Sum(line => line.Amount));

        await using var context = host.NewBillingContext();

        var charge = await context.AccountCharges.SingleAsync();

        Assert.Equal(AccountChargeStatus.Billed, charge.Status);
        Assert.Equal(bill.Id, charge.BillId);
    }

    [Fact]
    public async Task A_fee_lands_once_and_the_next_cycle_does_not_carry_it_again()
    {
        using var host = NewHost();

        var premise = Premise();
        var account = host.Accounts.Add(premise);

        host.Readings.Add(premise, 750m, Cycle, ReadAt);
        host.Readings.Add(premise, 800m, "2026-09", ReadAt.AddDays(30));

        await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        var second = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput("2026-09")));

        Assert.DoesNotContain(Assert.Single(second.Bills).Lines, line => line.Kind is ChargeKind.Fee);
        Assert.Equal(0m, second.Bills[0].FeeAmount);
    }

    [Fact]
    public async Task A_withdrawn_fee_never_reaches_a_bill()
    {
        using var host = NewHost();

        var premise = Premise();
        var account = host.Accounts.Add(premise);

        host.Readings.Add(premise, 750m, Cycle, ReadAt);

        var charge = await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        await host.WithChargesAsync(charges =>
            charges.CancelAsync(charge.Id, new CancelChargeInput("Raised against the wrong account.")));

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.DoesNotContain(Assert.Single(run.Bills).Lines, line => line.Kind is ChargeKind.Fee);
    }

    [Fact]
    public async Task A_fee_waiting_on_an_account_the_run_skipped_stays_waiting()
    {
        // A reading on the exception worklist raises no bill, so its account's fee is still there
        // for the next cycle rather than quietly consumed by a run that billed nothing.
        using var host = NewHost();

        var premise = Premise();
        var account = host.Accounts.Add(premise);

        host.Readings.Add(premise, 750m, Cycle, ReadAt, exceptionCode: "HighUsage");

        await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        var run = await host.WithBillsAsync(bills => bills.RunAsync(new RunBillingInput(Cycle)));

        Assert.Empty(run.Bills);

        await using var context = host.NewBillingContext();

        Assert.Equal(AccountChargeStatus.Pending, (await context.AccountCharges.SingleAsync()).Status);
    }

    [Fact]
    public async Task The_register_lists_what_is_still_waiting_for_a_bill()
    {
        using var host = NewHost();

        var account = host.Accounts.Add(Premise());

        var waiting = await host.WithChargesAsync(charges => charges.RaiseAsync(Reconnection(account.Id)));

        var withdrawn = await host.WithChargesAsync(charges => charges.RaiseAsync(
            new RaiseChargeInput(account.Id, FeeCode.MeterTest, "Customer asked for a meter test.")));

        await host.WithChargesAsync(charges =>
            charges.CancelAsync(withdrawn.Id, new CancelChargeInput("Customer withdrew the request.")));

        var pending = await host.WithChargesAsync(charges =>
            charges.ListAsync(new AccountChargeQuery(account.Id, PendingOnly: true)));

        Assert.Equal([waiting.Id], pending.Select(charge => charge.Id));
    }
}
