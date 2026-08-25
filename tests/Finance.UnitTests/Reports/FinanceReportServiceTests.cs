using GridCore.Contracts.Events;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Modules.Finance.Features.Journal;
using GridCore.Modules.Finance.Features.Reports;
using GridCore.Modules.Finance.UnitTests.Infrastructure;
using Chart = GridCore.Modules.Finance.Features.ChartOfAccounts.ChartOfAccounts;

namespace GridCore.Modules.Finance.UnitTests.Reports;

/// <summary>
/// The two reports the ledger answers. The assertion this work package exists to make is
/// <c>trial balance nets to zero</c>; the one that keeps it honest is that the receivables
/// subsidiary ledger sums to the receivables control account.
/// </summary>
public sealed class FinanceReportServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_empty_ledger_still_lists_the_whole_chart_and_balances()
    {
        using var host = new FinanceTestHost(new FakeClock(Now));

        var trialBalance = await host.WithReportsAsync(reports => reports.TrialBalanceAsync());

        // Every account, whether or not anything has been posted to it: a report that changed shape
        // as the demo ran would not let a reader tell an untouched account from a missing one.
        Assert.Equal(Chart.All.Count, trialBalance.Rows.Count);
        Assert.All(trialBalance.Rows, row => Assert.False(row.HasActivity));
        Assert.True(trialBalance.IsBalanced);
        Assert.Equal(0m, trialBalance.TotalDebits);
        Assert.Equal(0m, trialBalance.Difference);
    }

    [Fact]
    public async Task A_bill_a_correction_and_a_payment_leave_a_trial_balance_that_nets_to_zero()
    {
        using var host = new FinanceTestHost(new FakeClock(Now));

        var account = Guid.CreateVersion7(Now);
        var customer = Guid.CreateVersion7(Now);

        await host.PostAsync(FinancePostings.From(ABill(account, customer, 200m)));
        await host.PostAsync(FinancePostings.From(ACredit(account, customer, 50m, amountDue: 150m)));
        await host.PostAsync(FinancePostings.From(APayment(account, customer, 120m)));

        var trialBalance = await host.WithReportsAsync(reports => reports.TrialBalanceAsync());

        Assert.True(trialBalance.IsBalanced);
        Assert.Equal(0m, trialBalance.Difference);

        // Receivables: 200 raised, 50 credited away, 120 paid — 30 still owed.
        Assert.Equal(30m, BalanceOf(trialBalance, FinanceAccounts.AccountsReceivable));

        // Revenue: 200 earned, 50 given back.
        Assert.Equal(150m, BalanceOf(trialBalance, FinanceAccounts.Revenue));

        // Cash: what was actually taken.
        Assert.Equal(120m, BalanceOf(trialBalance, FinanceAccounts.Cash));
    }

    [Fact]
    public async Task A_charge_correction_moves_the_receivable_the_other_way()
    {
        using var host = new FinanceTestHost(new FakeClock(Now));

        var account = Guid.CreateVersion7(Now);
        var customer = Guid.CreateVersion7(Now);

        await host.PostAsync(FinancePostings.From(ABill(account, customer, 100m)));
        await host.PostAsync(FinancePostings.From(ACharge(account, customer, 25m, amountDue: 125m)));

        var trialBalance = await host.WithReportsAsync(reports => reports.TrialBalanceAsync());

        Assert.True(trialBalance.IsBalanced);
        Assert.Equal(125m, BalanceOf(trialBalance, FinanceAccounts.AccountsReceivable));
        Assert.Equal(125m, BalanceOf(trialBalance, FinanceAccounts.Revenue));
    }

    [Fact]
    public async Task A_trial_balance_reads_only_up_to_the_date_it_is_asked_for()
    {
        using var host = new FinanceTestHost(new FakeClock(Now));

        var account = Guid.CreateVersion7(Now);
        var customer = Guid.CreateVersion7(Now);

        await host.PostAsync(FinancePostings.From(ABill(account, customer, 100m, occurredAt: Now)));
        await host.PostAsync(FinancePostings.From(ABill(account, customer, 400m, occurredAt: Now.AddDays(40))));

        var trialBalance = await host.WithReportsAsync(reports =>
            reports.TrialBalanceAsync(DateOnly.FromDateTime(Now.UtcDateTime)));

        // The later bill is dated after the cut-off and is not in this period's figures.
        Assert.Equal(100m, BalanceOf(trialBalance, FinanceAccounts.AccountsReceivable));
        Assert.True(trialBalance.IsBalanced);
    }

    [Fact]
    public async Task The_receivables_ledger_says_who_owes_the_control_accounts_balance()
    {
        using var host = new FinanceTestHost(new FakeClock(Now));

        var owing = Guid.CreateVersion7(Now);
        var owingCustomer = Guid.CreateVersion7(Now);
        var settled = Guid.CreateVersion7(Now);
        var settledCustomer = Guid.CreateVersion7(Now);

        await host.PostAsync(FinancePostings.From(ABill(owing, owingCustomer, 200m)));
        await host.PostAsync(FinancePostings.From(APayment(owing, owingCustomer, 50m)));
        await host.PostAsync(FinancePostings.From(ABill(settled, settledCustomer, 80m)));
        await host.PostAsync(FinancePostings.From(APayment(settled, settledCustomer, 80m)));

        var receivables = await host.WithReportsAsync(reports => reports.ReceivablesAsync(new ReceivablesQuery()));
        var trialBalance = await host.WithReportsAsync(reports => reports.TrialBalanceAsync());

        // THE ASSERTION THAT KEEPS A SUBSIDIARY LEDGER HONEST: it sums to its control account.
        Assert.Equal(
            BalanceOf(trialBalance, FinanceAccounts.AccountsReceivable),
            receivables.TotalOutstanding);

        Assert.Equal(150m, receivables.TotalOutstanding);
        Assert.Equal(FinanceAccounts.AccountsReceivable, receivables.ControlAccountCode);

        // Most owed first: an AR worklist is read from the top.
        var first = receivables.Rows[0];

        Assert.Equal(owing, first.ServiceAccountId);
        Assert.Equal(owingCustomer, first.CustomerId);
        Assert.Equal(200m, first.Charged);
        Assert.Equal(50m, first.Settled);
        Assert.Equal(150m, first.Outstanding);
        Assert.Equal(2, first.PostingCount);

        Assert.Equal(0m, receivables.Rows.Single(row => row.ServiceAccountId == settled).Outstanding);
        Assert.Equal(0m, receivables.Unallocated);
    }

    [Fact]
    public async Task The_receivables_ledger_can_be_narrowed_to_what_is_still_owed()
    {
        using var host = new FinanceTestHost(new FakeClock(Now));

        var owing = Guid.CreateVersion7(Now);
        var settled = Guid.CreateVersion7(Now);
        var customer = Guid.CreateVersion7(Now);

        await host.PostAsync(FinancePostings.From(ABill(owing, customer, 200m)));
        await host.PostAsync(FinancePostings.From(ABill(settled, customer, 80m)));
        await host.PostAsync(FinancePostings.From(APayment(settled, customer, 80m)));

        var worklist = await host.WithReportsAsync(reports =>
            reports.ReceivablesAsync(new ReceivablesQuery(OutstandingOnly: true)));

        Assert.Equal(owing, Assert.Single(worklist.Rows).ServiceAccountId);
    }

    [Fact]
    public async Task A_credit_past_zero_shows_as_money_held_rather_than_money_owed()
    {
        // WP-2.3 and WP-2.4 both said an overpayment or an over-credit is Finance's to hold. This is
        // where it becomes visible: a negative receivable, reported rather than clamped to zero.
        using var host = new FinanceTestHost(new FakeClock(Now));

        var account = Guid.CreateVersion7(Now);
        var customer = Guid.CreateVersion7(Now);

        await host.PostAsync(FinancePostings.From(ABill(account, customer, 100m)));
        await host.PostAsync(FinancePostings.From(APayment(account, customer, 100m)));
        await host.PostAsync(FinancePostings.From(ACredit(account, customer, 15m, amountDue: 85m)));

        var receivables = await host.WithReportsAsync(reports => reports.ReceivablesAsync(new ReceivablesQuery()));

        Assert.Equal(-15m, receivables.TotalOutstanding);
        Assert.Equal(-15m, Assert.Single(receivables.Rows).Outstanding);
    }

    [Fact]
    public async Task The_receivables_ledger_can_be_narrowed_to_one_customer()
    {
        using var host = new FinanceTestHost(new FakeClock(Now));

        var mine = Guid.CreateVersion7(Now);
        var theirs = Guid.CreateVersion7(Now);

        await host.PostAsync(FinancePostings.From(ABill(Guid.CreateVersion7(Now), mine, 60m)));
        await host.PostAsync(FinancePostings.From(ABill(Guid.CreateVersion7(Now), theirs, 90m)));

        var receivables = await host.WithReportsAsync(reports =>
            reports.ReceivablesAsync(new ReceivablesQuery(CustomerId: mine)));

        Assert.Equal(60m, receivables.TotalOutstanding);
        Assert.Equal(mine, Assert.Single(receivables.Rows).CustomerId);
    }

    [Fact]
    public async Task A_goods_receipt_never_reaches_the_receivables_ledger()
    {
        // Failure path for the AR view: it reads the receivables control account and nothing else, so
        // a payable — which has no party at all — cannot turn up in a list of who owes money.
        using var host = new FinanceTestHost(new FakeClock(Now));

        await host.PostAsync(FinancePostings.From(GoodsReceived.For(
            Now,
            receiptId: Guid.CreateVersion7(Now),
            purchaseOrderId: Guid.CreateVersion7(Now),
            warehouseId: Guid.CreateVersion7(Now),
            vendorId: Guid.CreateVersion7(Now),
            currency: "USD",
            lines: [new GoodsReceivedLine(Guid.CreateVersion7(Now), "TRF-100", 2m, 100m)])));

        var receivables = await host.WithReportsAsync(reports => reports.ReceivablesAsync(new ReceivablesQuery()));
        var trialBalance = await host.WithReportsAsync(reports => reports.TrialBalanceAsync());

        Assert.Empty(receivables.Rows);
        Assert.Equal(0m, receivables.TotalOutstanding);

        // But it is on the ledger, on the two accounts it belongs on.
        Assert.True(trialBalance.IsBalanced);
        Assert.Equal(200m, BalanceOf(trialBalance, FinanceAccounts.Inventory));
        Assert.Equal(200m, BalanceOf(trialBalance, FinanceAccounts.AccountsPayable));
    }

    [Fact]
    public async Task The_ledger_lists_newest_first_and_carries_its_lines()
    {
        // The clock is advanced between the two postings on purpose. Entry ids are Guid v7 and the
        // listing orders on them; two entries minted in the same clock instant have no defined order
        // between them, which is a trap this repo has fallen into before.
        var clock = new FakeClock(Now);

        using var host = new FinanceTestHost(clock);

        var account = Guid.CreateVersion7(Now);
        var customer = Guid.CreateVersion7(Now);

        await host.PostAsync(FinancePostings.From(ABill(account, customer, 100m)));

        clock.Advance(TimeSpan.FromMinutes(5));

        await host.PostAsync(FinancePostings.From(APayment(account, customer, 40m)));

        var entries = await host.WithJournalAsync(journal =>
            journal.ListAsync(new JournalQuery()));

        Assert.Equal(2, entries.Count);
        Assert.Equal(FinancePostings.PaymentApprovedSource, entries[0].Source);
        Assert.All(entries, entry => Assert.Equal(2, entry.Lines.Count));
        Assert.All(entries, entry => Assert.All(entry.Lines, line => Assert.NotNull(line.Account)));
    }

    private static decimal BalanceOf(TrialBalance trialBalance, string accountCode) =>
        trialBalance.Rows.Single(row => row.AccountCode == accountCode).Balance;

    private static BillIssued ABill(Guid serviceAccountId, Guid customerId, decimal amount, DateTimeOffset? occurredAt = null) =>
        BillIssued.For(
            occurredAt ?? Now,
            billId: Guid.CreateVersion7(Now),
            billNumber: $"BIL-{Guid.NewGuid():N}"[..10],
            serviceAccountId: serviceAccountId,
            customerId: customerId,
            periodStart: new DateOnly(2026, 7, 1),
            periodEnd: new DateOnly(2026, 7, 31),
            dueDate: new DateOnly(2026, 8, 20),
            amount: amount,
            currency: "USD");

    private static PaymentApproved APayment(Guid serviceAccountId, Guid customerId, decimal amount) =>
        PaymentApproved.For(
            Now,
            paymentId: Guid.CreateVersion7(Now),
            serviceAccountId: serviceAccountId,
            customerId: customerId,
            billId: Guid.CreateVersion7(Now),
            amount: amount,
            currency: "USD",
            method: "card",
            providerReference: $"SIM-{Guid.NewGuid():N}"[..10]);

    private static BillAdjusted ACredit(Guid serviceAccountId, Guid customerId, decimal amount, decimal amountDue) =>
        AnAdjustment(serviceAccountId, customerId, "Credit", -amount, amountDue);

    private static BillAdjusted ACharge(Guid serviceAccountId, Guid customerId, decimal amount, decimal amountDue) =>
        AnAdjustment(serviceAccountId, customerId, "Charge", amount, amountDue);

    private static BillAdjusted AnAdjustment(
        Guid serviceAccountId,
        Guid customerId,
        string kind,
        decimal signedAmount,
        decimal amountDue) =>
        BillAdjusted.For(
            Now,
            billId: Guid.CreateVersion7(Now),
            billNumber: $"BIL-{Guid.NewGuid():N}"[..10],
            serviceAccountId: serviceAccountId,
            customerId: customerId,
            adjustmentId: Guid.CreateVersion7(Now),
            kind: kind,
            amount: signedAmount,
            amountDue: amountDue,
            currency: "USD",
            reason: "Estimated read corrected after a site visit.");
}
