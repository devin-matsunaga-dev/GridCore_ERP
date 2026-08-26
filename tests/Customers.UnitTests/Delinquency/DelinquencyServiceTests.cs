using System.Text.Json;
using GridCore.Contracts.Events;
using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Delinquency;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Delinquency;

/// <summary>
/// Delinquency, dunning and the statutory deposit offset over the customers schema (WP-2.19).
/// </summary>
/// <remarks>
/// SQLite in memory with the platform schema on the same connection, so these assert the thing that
/// matters about the evaluation: the deposit entries, the balance they moved, their audit entries
/// and the events Finance posts from are all one transaction. <c>IBillDirectory</c> is a double —
/// what an account owes is Billing's register, and this module may never read it.
/// </remarks>
public class DelinquencyServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private static CustomersTestHost NewHost(ICurrentUser? user = null) =>
        new(new FakeClock(Now), user ?? new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    /// <summary>A customer with one open electricity account, and the account.</summary>
    private static async Task<(Customer Customer, ServiceAccount Account)> AServedCustomerAsync(CustomersTestHost host)
    {
        var customer = await host.WithCustomersAsync(customers =>
            customers.RegisterAsync(new RegisterCustomerInput("Sablan Family Residence", CustomerClass.Residential)));

        var premise = await host.WithLocationsAsync(locations => locations.RegisterAsync(
            new ServiceLocationInput(
                Address.Create("14 Sablan Street", "Songsong", "Rota", "MP"),
                "Meter on the north wall")));

        var account = await host.WithAccountsAsync(accounts =>
            accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, premise.Id, ServiceType.Electricity)));

        return (customer, account);
    }

    /// <summary>Puts a deposit on the customer's ledger, so there is something to offset.</summary>
    private static Task<DepositEntry> ADepositAsync(CustomersTestHost host, Guid customerId, decimal amount) =>
        host.WithDepositsAsync(deposits =>
            deposits.CollectAsync(customerId, new CollectDepositInput(amount, Reason: "Taken at the counter.")));

    /// <summary>Records that a disconnection notice went out <paramref name="daysAgo"/> days ago.</summary>
    private static Task<DunningNotice> ANoticeAsync(
        CustomersTestHost host,
        Guid accountId,
        int daysAgo,
        DunningNoticeType noticeType = DunningNoticeType.Disconnection) =>
        host.WithDelinquencyAsync(delinquency =>
            delinquency.ServeAsync(accountId, new ServeNoticeInput(noticeType, Today.AddDays(-daysAgo))));

    [Fact]
    public async Task The_picture_reads_the_arrears_off_Billings_register_and_never_a_bill_table()
    {
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-60), 120.00m);
        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(14), 80.00m);

        var picture = await host.WithDelinquencyAsync(delinquency => delinquency.GetAsync(account.Id));

        Assert.Equal(200.00m, picture.Arrears.OutstandingAmount);
        Assert.Equal(120.00m, picture.Arrears.PastDueAmount);
        Assert.Equal(60, picture.Arrears.DaysPastDue);
        Assert.Equal(account.AccountNumber, picture.AccountNumber);
        Assert.Equal(customer.Id, picture.CustomerId);
    }

    [Fact]
    public async Task The_picture_moves_nothing()
    {
        // THE SPLIT THE DESIGN TURNS ON. A screen showing what would happen must not be a screen
        // that makes it happen — a GET that moved money would move it again on every refresh.
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        await ADepositAsync(host, customer.Id, 300.00m);
        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-90), 200.00m);

        var picture = await host.WithDelinquencyAsync(delinquency => delinquency.GetAsync(account.Id));

        Assert.Equal(200.00m, picture.Eligibility.OffsetAmount);
        Assert.False(picture.Eligibility.IsOffsetApplied);

        await using var database = host.NewCustomersContext();

        Assert.Equal(300.00m, (await database.Customers.SingleAsync()).DepositHeld);
        Assert.Equal(1, await database.DepositEntries.CountAsync());
    }

    [Fact]
    public async Task Evaluating_applies_a_300_deposit_to_200_of_arrears_and_leaves_the_account_ineligible()
    {
        // THE STATUTE, end to end: the offset is a real DepositEntry against a real bill, and the
        // account it clears is not eligible for disconnection at all.
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        await ADepositAsync(host, customer.Id, 300.00m);

        var bill = host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-90), 200.00m);

        await ANoticeAsync(host, account.Id, daysAgo: 20);

        var evaluation = await host.WithDelinquencyAsync(delinquency =>
            delinquency.EvaluateAsync(account.Id, new EvaluateDisconnectionInput(Today)));

        Assert.False(evaluation.Eligibility.IsEligible);
        Assert.Equal(200.00m, evaluation.OffsetAmount);
        Assert.Equal(0m, evaluation.Eligibility.ArrearsAfterOffset);
        Assert.True(evaluation.Eligibility.IsOffsetApplied);

        var entry = Assert.Single(evaluation.OffsetEntries);

        Assert.Equal(DepositEntryKind.Applied, entry.Kind);
        Assert.Equal(200.00m, entry.Amount);
        Assert.Equal(bill.Id, entry.BillId);
        Assert.Equal(100.00m, entry.BalanceAfter);
    }

    [Fact]
    public async Task Evaluating_applies_a_100_deposit_to_200_of_arrears_and_leaves_it_eligible()
    {
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        await ADepositAsync(host, customer.Id, 100.00m);
        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-90), 200.00m);

        await ANoticeAsync(host, account.Id, daysAgo: 20);

        var evaluation = await host.WithDelinquencyAsync(delinquency =>
            delinquency.EvaluateAsync(account.Id, new EvaluateDisconnectionInput(Today)));

        Assert.True(evaluation.Eligibility.IsEligible);
        Assert.Equal(100.00m, evaluation.OffsetAmount);
        Assert.Equal(100.00m, evaluation.Eligibility.ArrearsAfterOffset);
        Assert.Equal(0m, evaluation.Eligibility.DepositHeldAfterOffset);
    }

    [Fact]
    public async Task The_offset_settles_the_oldest_bill_first()
    {
        // What "qualifying past-due amounts" means to a debtor. A deposit that settled the newest
        // bill and left a year-old one standing would leave the account exactly as delinquent as it
        // was and cost the customer their deposit.
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        await ADepositAsync(host, customer.Id, 150.00m);

        var newest = host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-30), 100.00m);
        var oldest = host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-90), 100.00m);

        await ANoticeAsync(host, account.Id, daysAgo: 20);

        var evaluation = await host.WithDelinquencyAsync(delinquency =>
            delinquency.EvaluateAsync(account.Id, new EvaluateDisconnectionInput(Today)));

        Assert.Equal(2, evaluation.OffsetEntries.Count);

        // One movement per bill, because a DepositEntry names the bill it settled and Billing
        // reduces exactly that bill when it consumes the event.
        Assert.Equal(oldest.Id, evaluation.OffsetEntries[0].BillId);
        Assert.Equal(100.00m, evaluation.OffsetEntries[0].Amount);
        Assert.Equal(newest.Id, evaluation.OffsetEntries[1].BillId);
        Assert.Equal(50.00m, evaluation.OffsetEntries[1].Amount);
    }

    [Fact]
    public async Task Every_offset_entry_names_the_statutory_basis()
    {
        // WORK_PACKAGES.md: "a legally obliged movement should defend itself from the trail without
        // anyone remembering why it happened".
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        await ADepositAsync(host, customer.Id, 100.00m);
        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-90), 200.00m);

        await ANoticeAsync(host, account.Id, daysAgo: 20);

        var evaluation = await host.WithDelinquencyAsync(delinquency =>
            delinquency.EvaluateAsync(account.Id, new EvaluateDisconnectionInput(Today)));

        Assert.All(
            evaluation.OffsetEntries,
            entry => Assert.Contains(StatutoryBasis.PublicLaw1617, entry.Reason!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_offset_is_audited_and_publishes_the_fact_Finance_posts_from()
    {
        // Invariants 1 and 2, in one transaction with the ledger row: CustomerDepositApplied is what
        // Finance turns into Dr Customer Deposits / Cr AR and Billing turns into a reduced bill.
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        await ADepositAsync(host, customer.Id, 100.00m);
        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-90), 200.00m);

        await ANoticeAsync(host, account.Id, daysAgo: 20);

        await host.WithDelinquencyAsync(delinquency =>
            delinquency.EvaluateAsync(account.Id, new EvaluateDisconnectionInput(Today)));

        var applied = host.Events.Single<CustomerDepositApplied>();

        Assert.Equal(100.00m, applied.Amount);
        Assert.Equal(0m, applied.BalanceAfter);

        await using var platform = host.NewPlatformContext();

        // The movement's own entry, written by the deposit ledger.
        Assert.Equal(
            1,
            await platform.AuditEntries.CountAsync(entry => entry.Action == AuditActions.CustomerDepositApplied));

        // And the entry that says why the movement was made at all.
        var evaluation = await platform.AuditEntries
            .SingleAsync(entry => entry.Action == AuditActions.DisconnectionEligibilityEvaluated);

        Assert.Equal(AuditEntityTypes.ServiceAccount, evaluation.EntityType);
        Assert.Equal(account.Id.ToString(), evaluation.EntityId);
        Assert.Contains(StatutoryBasis.PublicLaw1617, evaluation.AfterJson!, StringComparison.Ordinal);

        var snapshot = JsonSerializer.Deserialize<DisconnectionEvaluationSnapshot>(
            evaluation.AfterJson!,
            AuditJson.Options)!;

        Assert.Equal(200.00m, snapshot.ArrearsBeforeOffset);
        Assert.Equal(100.00m, snapshot.OffsetAmount);
        Assert.Equal(100.00m, snapshot.ArrearsAfterOffset);
        Assert.True(snapshot.IsEligible);
    }

    [Fact]
    public async Task Evaluating_without_the_deposit_permission_is_refused()
    {
        // THE FAILURE PATH THE VERIFY LIST NAMES. Demanded in the service before anything is read,
        // so the refusal does not depend on the customer happening to hold a deposit to move.
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        await ADepositAsync(host, customer.Id, 300.00m);
        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-90), 200.00m);

        await ANoticeAsync(host, account.Id, daysAgo: 20);

        var clerk = FakeCurrentUser.Holding(Permissions.Customers.Read, Permissions.Customers.Write);

        var refusal = await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.AsAsync(clerk, delinquency =>
                delinquency.EvaluateAsync(account.Id, new EvaluateDisconnectionInput(Today))));

        Assert.Contains(Permissions.Customers.Deposit, refusal.Message, StringComparison.Ordinal);

        await using var database = host.NewCustomersContext();

        // Nothing moved: the deposit is exactly where the collection left it.
        Assert.Equal(300.00m, (await database.Customers.SingleAsync()).DepositHeld);
        Assert.Equal(1, await database.DepositEntries.CountAsync());
    }

    [Fact]
    public async Task An_account_holding_no_deposit_is_still_refused_without_the_permission()
    {
        // The reason the gate is demanded before the arrears are read rather than left to the deposit
        // ledger: an account with nothing to offset would otherwise slip past it simply because there
        // was no movement to refuse.
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-90), 200.00m);

        await ANoticeAsync(host, account.Id, daysAgo: 20);

        var clerk = FakeCurrentUser.Holding(Permissions.Customers.Read);

        await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.AsAsync(clerk, delinquency =>
                delinquency.EvaluateAsync(account.Id, new EvaluateDisconnectionInput(Today))));
    }

    [Fact]
    public async Task Reading_the_picture_needs_no_deposit_permission()
    {
        // Quoting a shortfall down the telephone is what a rep does all day; spending the deposit is
        // not. The same split WP-2.17's re-assessment drew.
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-90), 200.00m);

        var clerk = FakeCurrentUser.Holding(Permissions.Customers.Read);

        var picture = await host.AsAsync(clerk, delinquency => delinquency.GetAsync(account.Id, Today));

        Assert.Equal(200.00m, picture.Arrears.PastDueAmount);
    }

    [Fact]
    public async Task Serving_a_notice_records_what_the_customer_was_told_on_the_day_it_went_out()
    {
        // The record is the evidence. What matters is what the customer was told, not what they owe
        // now — so the arrears is read as it stood on the day the notice was served.
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-90), 200.00m);

        var notice = await ANoticeAsync(host, account.Id, daysAgo: 5);

        Assert.Equal(DunningNoticeType.Disconnection, notice.NoticeType);
        Assert.Equal(Today.AddDays(-5), notice.ServedOn);
        Assert.Equal(200.00m, notice.ArrearsAmount);
        Assert.Equal(85, notice.DaysPastDue);
        Assert.Equal(10, notice.WaitingPeriodDays);
        Assert.Equal(Today.AddDays(5), notice.EffectiveFrom);

        await using var database = host.NewCustomersContext();

        Assert.Equal(1, await database.DunningNotices.CountAsync());

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries.SingleAsync(candidate =>
            candidate.Action == AuditActions.DunningNoticeServed);

        Assert.Equal(AuditEntityTypes.DunningNotice, entry.EntityType);
        Assert.Equal(notice.Id.ToString(), entry.EntityId);
    }

    [Fact]
    public async Task A_notice_the_account_has_not_earned_is_refused()
    {
        // A disconnection notice served on somebody eleven days late starts a statutory clock the
        // utility was not entitled to start — and it is exactly the record that would later be
        // produced to justify cutting them off.
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-11), 200.00m);

        var refusal = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithDelinquencyAsync(delinquency =>
                delinquency.ServeAsync(account.Id, new ServeNoticeInput(DunningNoticeType.Disconnection, Today))));

        Assert.Contains("had not earned", refusal.Message, StringComparison.Ordinal);

        await using var database = host.NewCustomersContext();

        Assert.Equal(0, await database.DunningNotices.CountAsync());
    }

    [Fact]
    public async Task A_notice_is_refused_where_the_account_owes_less_than_the_step_asks_for()
    {
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-90), 5.00m);

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithDelinquencyAsync(delinquency =>
                delinquency.ServeAsync(account.Id, new ServeNoticeInput(DunningNoticeType.Reminder, Today))));
    }

    [Fact]
    public async Task Eligibility_reads_the_MOST_RECENT_disconnection_notice()
    {
        // An account that cleared its arrears, fell behind again and was served again is entitled to
        // the second notice's waiting period — judging on the first would cut somebody off on a clock
        // that ran out while they were up to date.
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-200), 200.00m);

        await ANoticeAsync(host, account.Id, daysAgo: 120);
        await ANoticeAsync(host, account.Id, daysAgo: 2);

        var picture = await host.WithDelinquencyAsync(delinquency => delinquency.GetAsync(account.Id, Today));

        Assert.Equal(Today.AddDays(-2), picture.Eligibility.DisconnectionNoticeServedOn);
        Assert.False(picture.Eligibility.IsEligible);
        Assert.Contains(DisconnectionRules.WaitingPeriodTest, picture.Eligibility.Blockers);
    }

    [Fact]
    public async Task An_active_payment_arrangement_suppresses_disconnection()
    {
        // The fourth test, through the seam WP-2.20 will answer for real.
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-90), 200.00m);

        await ANoticeAsync(host, account.Id, daysAgo: 20);

        host.Arrangements.Active(account.Id);

        var picture = await host.WithDelinquencyAsync(delinquency => delinquency.GetAsync(account.Id, Today));

        Assert.False(picture.Eligibility.IsEligible);
        Assert.Equal([DisconnectionRules.ArrangementTest], picture.Eligibility.Blockers);
        Assert.Equal("Active", picture.Eligibility.Arrangement!.Status);
    }

    [Fact]
    public async Task The_picture_says_which_step_the_account_has_reached_and_which_have_been_served()
    {
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-46), 200.00m);

        await ANoticeAsync(host, account.Id, daysAgo: 30, DunningNoticeType.Reminder);

        var picture = await host.WithDelinquencyAsync(delinquency => delinquency.GetAsync(account.Id, Today));

        Assert.Equal(DunningNoticeType.Disconnection, picture.DueStep!.NoticeType);
        Assert.Equal(DunningNoticeType.Reminder, Assert.Single(picture.Notices).NoticeType);
        Assert.Equal(DunningSequence.All.Count, picture.Steps.Count);
    }

    [Fact]
    public async Task Evaluating_an_account_with_nothing_past_due_moves_nothing_and_answers_no()
    {
        using var host = NewHost();

        var (customer, account) = await AServedCustomerAsync(host);

        await ADepositAsync(host, customer.Id, 300.00m);
        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(14), 200.00m);

        var evaluation = await host.WithDelinquencyAsync(delinquency =>
            delinquency.EvaluateAsync(account.Id, new EvaluateDisconnectionInput(Today)));

        Assert.False(evaluation.Eligibility.IsEligible);
        Assert.Empty(evaluation.OffsetEntries);
        Assert.Equal(0m, evaluation.OffsetAmount);

        await using var database = host.NewCustomersContext();

        Assert.Equal(300.00m, (await database.Customers.SingleAsync()).DepositHeld);
    }

    [Fact]
    public async Task There_is_no_such_account_answers_404_shaped()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<ServiceAccountNotFoundException>(() =>
            host.WithDelinquencyAsync(delinquency => delinquency.GetAsync(Guid.CreateVersion7())));
    }
}
