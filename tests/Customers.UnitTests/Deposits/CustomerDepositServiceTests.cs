using System.Text.Json;
using GridCore.Contracts.Events;
using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Deposits;

/// <summary>
/// The deposit lifecycle over the customers schema: the permission gate, the audit trail, the
/// events Finance and Billing post from, and the questions asked of Billing before money moves.
/// </summary>
/// <remarks>
/// SQLite in memory with the platform schema on the same connection, so these assert the thing that
/// matters about a money write: the ledger row, the balance it moved, its audit entry and its outbox
/// row are one transaction. <c>IBillDirectory</c> is a double — a <c>billing</c> schema is exactly
/// what this module may never know about.
/// </remarks>
public class CustomerDepositServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private static CustomersTestHost NewHost(ICurrentUser? user = null) =>
        new(new FakeClock(Now), user ?? new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    private static Task<Customer> ARegisteredCustomerAsync(
        CustomersTestHost host,
        CustomerClass customerClass = CustomerClass.Residential) =>
        host.WithCustomersAsync(customers =>
            customers.RegisterAsync(new RegisterCustomerInput("Sablan Family Residence", customerClass)));

    /// <summary>
    /// A customer with one open electricity account — since WP-2.17, what a customer has to have
    /// before the schedule asks them for anything.
    /// </summary>
    /// <remarks>
    /// A deposit is assessed against the supplies somebody takes, so a bare customer record is
    /// assessed at nothing at all. That is the right answer and it is asserted on its own below; it
    /// is not what most of these tests are about, so they take an account.
    /// </remarks>
    private static async Task<Customer> AServedCustomerAsync(
        CustomersTestHost host,
        CustomerClass customerClass = CustomerClass.Residential)
    {
        var customer = await ARegisteredCustomerAsync(host, customerClass);

        var premise = await host.WithLocationsAsync(locations => locations.RegisterAsync(
            new ServiceLocationInput(
                Address.Create("14 Sablan Street", "Songsong", "Rota", "MP"),
                "Meter on the north wall")));

        await host.WithAccountsAsync(accounts =>
            accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, premise.Id, ServiceType.Electricity)));

        return customer;
    }

    [Fact]
    public async Task Collecting_a_deposit_writes_the_entry_moves_the_balance_and_publishes_the_fact()
    {
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        var entry = await host.WithDepositsAsync(deposits =>
            deposits.CollectAsync(customer.Id, new CollectDepositInput(75.00m, IsInterestBearing: true, "Taken at the counter.")));

        await using var database = host.NewCustomersContext();

        var stored = await database.DepositEntries.SingleAsync();

        Assert.Equal(entry.Id, stored.Id);
        Assert.Equal(DepositEntryKind.Collected, stored.Kind);
        Assert.Equal(75.00m, stored.Amount);
        Assert.True(stored.IsInterestBearing);

        // The projection moved with it, in the same transaction.
        Assert.Equal(75.00m, (await database.Customers.SingleAsync()).DepositHeld);

        var published = host.Events.Single<CustomerDepositCollected>();

        Assert.Equal(entry.Id, published.DepositEntryId);
        Assert.Equal(customer.AccountNumber, published.AccountNumber);
        Assert.Equal(75.00m, published.Amount);
        Assert.Equal(75.00m, published.BalanceAfter);
        Assert.True(published.IsInterestBearing);
    }

    [Fact]
    public async Task A_collection_is_audited_with_what_the_schedule_asked_for_beside_what_was_taken()
    {
        // Invariant 5, and WP-2.8's shape kept: the entry carries the assessed figure, the collected
        // figure and the rules that said so, which is the only place that difference is recorded.
        using var host = NewHost();

        var customer = await AServedCustomerAsync(host);

        await host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(25.00m)));

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries
            .SingleAsync(candidate => candidate.Action == AuditActions.CustomerDepositCollected);

        Assert.Equal(AuditEntityTypes.Customer, entry.EntityType);
        Assert.Equal(customer.Id.ToString(), entry.EntityId);

        var snapshot = JsonSerializer.Deserialize<DepositCollectionSnapshot>(entry.AfterJson!, AuditJson.Options);

        Assert.NotNull(snapshot);
        Assert.Equal(75.00m, snapshot.AssessedAmount);
        Assert.Equal(25.00m, snapshot.CollectedAmount);
        Assert.Equal(25.00m, snapshot.BalanceAfter);

        // The rule rows that answered — one per open account since WP-2.17 keyed the schedule on
        // the service too — so a figure on a customer's record can be traced to the reference data
        // that explains it rather than to whoever typed it.
        Assert.All(snapshot.RuleIds, ruleId => Assert.NotEqual(Guid.Empty, ruleId));
    }

    [Theory]
    [InlineData("collect")]
    [InlineData("apply")]
    [InlineData("refund")]
    public async Task A_deposit_movement_without_the_permission_is_refused_and_writes_nothing(string act)
    {
        // Failure path, and WORK_PACKAGES.md's "deposit action without permission → 403". The caller
        // holds customers.write, which opens every other door in this module and none of this one.
        using var host = NewHost(FakeCurrentUser.Holding(Permissions.Customers.Write, Permissions.Customers.Read));

        var customer = await ARegisteredCustomerAsync(host);

        // Taken by somebody who does hold it, so there is a balance for the refused acts to aim at.
        // The collection runs through a second host over the same in-memory database, because this
        // one's caller is the narrowed rep the test is about.
        var bill = host.Bills.Add(customer.Id, amountDue: 120.00m);

        await host.AsAsync(
            new FakeCurrentUser("auth0|finance", "Joe Aldan"),
            deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(75.00m)));

        var refused = await Assert.ThrowsAsync<RegistryPermissionException>(() => act switch
        {
            "collect" => host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(10.00m))),
            "apply" => host.WithDepositsAsync(deposits => deposits.ApplyAsync(customer.Id, new ApplyDepositInput(bill.Id, 10.00m))),
            _ => host.WithDepositsAsync(deposits => deposits.RefundAsync(customer.Id, new RefundDepositInput(10.00m))),
        });

        Assert.Contains(Permissions.Customers.Deposit, refused.Message, StringComparison.Ordinal);

        await using var database = host.NewCustomersContext();

        // The one entry is the collection above; the refused act added nothing and moved nothing.
        Assert.Single(await database.DepositEntries.ToListAsync());
        Assert.Equal(75.00m, (await database.Customers.SingleAsync()).DepositHeld);
    }

    [Fact]
    public async Task Applying_a_deposit_to_a_bill_reduces_the_balance_and_names_the_bill()
    {
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        await host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(75.00m)));

        var bill = host.Bills.Add(customer.Id, amountDue: 120.00m);

        var entry = await host.WithDepositsAsync(deposits =>
            deposits.ApplyAsync(customer.Id, new ApplyDepositInput(bill.Id, 40.00m, "Customer asked us to use the deposit.")));

        Assert.Equal(DepositEntryKind.Applied, entry.Kind);
        Assert.Equal(35.00m, entry.BalanceAfter);
        Assert.Equal(bill.Id, entry.BillId);

        // The BILL's currency, not the schedule's: the receivable being relieved is denominated in
        // what the bill was raised in.
        Assert.Equal(bill.Currency, entry.Currency);

        var published = host.Events.Single<CustomerDepositApplied>();

        Assert.Equal(bill.Id, published.BillId);
        Assert.Equal(bill.BillNumber, published.BillNumber);
        Assert.Equal(bill.ServiceAccountId, published.ServiceAccountId);
        Assert.Equal(40.00m, published.Amount);

        // It went through the seam rather than reaching into a billing schema.
        Assert.Contains(bill.Id, host.Bills.Lookups);
    }

    [Fact]
    public async Task Applying_more_than_the_bill_has_outstanding_is_refused()
    {
        // Failure path, and WORK_PACKAGES.md's own words. Refused rather than absorbed: money left
        // over would be a credit with no record of where it went, and the deposit is the one place
        // in GridCore it could have stayed.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host, CustomerClass.Commercial);

        await host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(450.00m)));

        // 100 owed, 30 already paid — so 70 is what may be settled, not the 100 the bill was for.
        var bill = host.Bills.Add(customer.Id, amountDue: 100.00m, amountPaid: 30.00m);

        var refused = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithDepositsAsync(deposits => deposits.ApplyAsync(customer.Id, new ApplyDepositInput(bill.Id, 70.01m))));

        Assert.Contains("70.00", refused.Message, StringComparison.Ordinal);

        await using var database = host.NewCustomersContext();

        Assert.Equal(450.00m, (await database.Customers.SingleAsync()).DepositHeld);
        Assert.Empty(host.Events.Published.OfType<CustomerDepositApplied>());
    }

    [Fact]
    public async Task Applying_exactly_the_outstanding_balance_is_allowed()
    {
        // The boundary the refusal above sits one cent past. A bill settled to the penny out of the
        // deposit is the ordinary case, and an off-by-one here would refuse it.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host, CustomerClass.Commercial);

        await host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(450.00m)));

        var bill = host.Bills.Add(customer.Id, amountDue: 100.00m, amountPaid: 30.00m);

        var entry = await host.WithDepositsAsync(deposits =>
            deposits.ApplyAsync(customer.Id, new ApplyDepositInput(bill.Id, 70.00m)));

        Assert.Equal(380.00m, entry.BalanceAfter);
    }

    [Fact]
    public async Task A_bill_that_is_not_owed_cannot_be_settled_from_a_deposit()
    {
        // Failure path: a draft was never sent and a cancelled bill was withdrawn, so neither is
        // money anybody owes. Whether a bill is outstanding is Billing's answer, arriving on the
        // summary — this module does not re-derive it from the status name.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        await host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(75.00m)));

        var cancelled = host.Bills.Add(customer.Id, amountDue: 50.00m, status: "Cancelled");

        var refused = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithDepositsAsync(deposits => deposits.ApplyAsync(customer.Id, new ApplyDepositInput(cancelled.Id, 10.00m))));

        Assert.Contains("Cancelled", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_customers_bill_cannot_be_settled_from_this_deposit()
    {
        // Failure path, and the one that would be a real loss if it were missed: a mistyped bill id
        // would otherwise spend one customer's deposit on somebody else's debt.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);
        var stranger = await host.WithCustomersAsync(customers =>
            customers.RegisterAsync(new RegisterCustomerInput("Taisacan Household", CustomerClass.Residential)));

        await host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(75.00m)));

        var theirs = host.Bills.Add(stranger.Id, amountDue: 50.00m);

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithDepositsAsync(deposits => deposits.ApplyAsync(customer.Id, new ApplyDepositInput(theirs.Id, 10.00m))));

        await using var database = host.NewCustomersContext();

        Assert.Equal(75.00m, (await database.Customers.SingleAsync(row => row.Id == customer.Id)).DepositHeld);
    }

    [Fact]
    public async Task A_bill_that_does_not_exist_is_refused_rather_than_treated_as_nothing_owed()
    {
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        await host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(75.00m)));

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithDepositsAsync(deposits =>
                deposits.ApplyAsync(customer.Id, new ApplyDepositInput(Guid.CreateVersion7(Now), 10.00m))));
    }

    [Fact]
    public async Task Refunding_gives_the_money_back_and_publishes_the_reverse_of_the_collection()
    {
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        await host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(75.00m)));

        var entry = await host.WithDepositsAsync(deposits =>
            deposits.RefundAsync(customer.Id, new RefundDepositInput(75.00m, "Account closed, final bill settled.")));

        Assert.Equal(DepositEntryKind.Refunded, entry.Kind);
        Assert.Equal(0m, entry.BalanceAfter);

        var published = host.Events.Single<CustomerDepositRefunded>();

        Assert.Equal(75.00m, published.Amount);
        Assert.Equal(0m, published.BalanceAfter);
        Assert.Equal("Account closed, final bill settled.", published.Reason);

        await using var database = host.NewCustomersContext();

        // The collection is still there, untouched. A refund is a NEW entry, never an unwinding.
        Assert.Equal(2, await database.DepositEntries.CountAsync());
        Assert.Equal(0m, (await database.Customers.SingleAsync()).DepositHeld);
    }

    [Fact]
    public async Task Refunding_more_than_is_held_is_refused_and_leaves_the_balance_alone()
    {
        // Failure path, from the aggregate rather than a copy of the rule in this service.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        await host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(75.00m)));

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithDepositsAsync(deposits => deposits.RefundAsync(customer.Id, new RefundDepositInput(100.00m))));

        await using var database = host.NewCustomersContext();

        Assert.Equal(75.00m, (await database.Customers.SingleAsync()).DepositHeld);
        Assert.Empty(host.Events.Published.OfType<CustomerDepositRefunded>());
    }

    [Fact]
    public async Task Every_movement_is_audited_under_an_action_of_its_own()
    {
        // "What has this customer's deposit been spent on" is a filter on an action name, not a
        // diff somebody has to read every deposit entry to work out.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        await host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(75.00m)));

        var bill = host.Bills.Add(customer.Id, amountDue: 30.00m);

        await host.WithDepositsAsync(deposits => deposits.ApplyAsync(customer.Id, new ApplyDepositInput(bill.Id, 30.00m)));
        await host.WithDepositsAsync(deposits => deposits.RefundAsync(customer.Id, new RefundDepositInput(45.00m)));

        await using var platform = host.NewPlatformContext();

        var actions = await platform.AuditEntries
            .Where(entry => entry.EntityId == customer.Id.ToString())
            .Select(entry => entry.Action)
            .ToListAsync();

        Assert.Contains(AuditActions.CustomerDepositCollected, actions);
        Assert.Contains(AuditActions.CustomerDepositApplied, actions);
        Assert.Contains(AuditActions.CustomerDepositRefunded, actions);
    }

    [Fact]
    public async Task A_failed_movement_leaves_no_entry_no_audit_row_and_no_event()
    {
        // The shared unit of work doing what it exists for: the aggregate's guard throws inside the
        // transaction, so the ledger row, its audit entry and its outbox row roll back together.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithDepositsAsync(deposits => deposits.RefundAsync(customer.Id, new RefundDepositInput(10.00m))));

        await using var database = host.NewCustomersContext();
        await using var platform = host.NewPlatformContext();

        Assert.Empty(await database.DepositEntries.ToListAsync());
        Assert.Empty(await platform.AuditEntries.Where(entry => entry.Action == AuditActions.CustomerDepositRefunded).ToListAsync());
        Assert.Empty(host.Events.Published.OfType<CustomerDepositRefunded>());
    }

    [Fact]
    public async Task A_movement_against_a_customer_who_does_not_exist_is_a_not_found()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<CustomerNotFoundException>(() =>
            host.WithDepositsAsync(deposits =>
                deposits.CollectAsync(Guid.CreateVersion7(Now), new CollectDepositInput(75.00m))));
    }

    [Fact]
    public async Task The_ledger_reads_back_newest_first_with_the_balance_and_the_assessment()
    {
        // The clock ADVANCES between the two movements, deliberately. Entry ids are Guid v7 minted
        // from the clock, so two movements at the same instant differ only in their random bits and
        // "newest first" would be a coin toss — the one thing this test is about.
        var clock = new FakeClock(Now);

        using var host = new CustomersTestHost(clock, new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

        var customer = await AServedCustomerAsync(host);

        await host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(50.00m)));

        clock.Advance(TimeSpan.FromMinutes(5));

        await host.WithDepositsAsync(deposits => deposits.RefundAsync(customer.Id, new RefundDepositInput(20.00m)));

        var ledger = await host.WithDepositsAsync(deposits => deposits.GetAsync(customer.Id));

        Assert.Equal(30.00m, ledger.Balance);
        Assert.Equal(customer.AccountNumber, ledger.AccountNumber);

        // The whole re-assessment rides along so a screen can say whether the customer is short of
        // it without a second request. One open electric account, so it is the residential electric
        // rule's $75 floor and nothing else.
        Assert.Equal(75.00m, ledger.Requirement.RequiredAmount);
        Assert.Equal(CustomerClass.Residential, ledger.Requirement.CustomerClass);
        Assert.Equal(ServiceType.Electricity, Assert.Single(ledger.Requirement.Accounts).Assessment.ServiceType);

        Assert.Equal(
            [DepositEntryKind.Refunded, DepositEntryKind.Collected],
            ledger.Entries.Select(entry => entry.Kind));
    }

    [Fact]
    public async Task A_customer_who_has_never_paid_a_deposit_reads_back_empty_rather_than_missing()
    {
        // An empty ledger is an ordinary answer, not a 404: every customer has a deposit position,
        // and most of them are zero.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        var ledger = await host.WithDepositsAsync(deposits => deposits.GetAsync(customer.Id));

        Assert.Equal(0m, ledger.Balance);
        Assert.Empty(ledger.Entries);
        Assert.False(ledger.IsInterestBearing);

        // Nothing held and nothing asked for: a customer record with no service account takes no
        // supply, and since WP-2.17 the schedule is keyed on the supply. A shortfall here would be
        // the utility chasing a deposit for a connection nobody has applied for.
        Assert.Equal(0m, ledger.Requirement.RequiredAmount);
        Assert.Equal(0m, ledger.Requirement.ShortfallAmount);
        Assert.Empty(ledger.Requirement.Accounts);
        Assert.True(ledger.Requirement.IsCovered);
    }

    [Fact]
    public async Task A_served_customer_is_asked_for_the_schedule_figure_of_the_supply_they_take()
    {
        using var host = NewHost();

        var customer = await AServedCustomerAsync(host);

        var ledger = await host.WithDepositsAsync(deposits => deposits.GetAsync(customer.Id));

        var line = Assert.Single(ledger.Requirement.Accounts);

        Assert.Equal(ServiceType.Electricity, line.Assessment.ServiceType);
        Assert.Equal(75.00m, ledger.Requirement.RequiredAmount);
        Assert.Equal(75.00m, ledger.Requirement.ShortfallAmount);
        Assert.False(ledger.Requirement.IsCovered);

        // Nothing has been read at the premise, so the usage half of the rule has nothing to price
        // and the published floor answers — never zero.
        Assert.False(line.HasUsageHistory);
        Assert.False(line.Assessment.IsUsageBased);
    }

    [Fact]
    public async Task Reading_a_ledger_for_a_customer_who_does_not_exist_is_a_not_found() =>
        await Assert.ThrowsAsync<CustomerNotFoundException>(async () =>
        {
            using var host = NewHost();

            await host.WithDepositsAsync(deposits => deposits.GetAsync(Guid.CreateVersion7(Now)));
        });
}
