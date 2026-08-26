using GridCore.Contracts.Events;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>
/// The service account registry over the real EF model, on SQLite in-memory. What these prove that
/// the aggregate tests cannot: the account, its history line, its audit entry and its event are one
/// transaction; the number series continues; and the cross-registry rules — an account needs a
/// customer who may take service and a premise nobody else is already served at — are enforced
/// where only the service can see them.
/// </summary>
public class ServiceAccountServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static CustomersTestHost NewHost(FakeClock? clock = null) =>
        new(clock ?? new FakeClock(Now), new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    private static Task<Customer> ACustomerAsync(CustomersTestHost host, string name = "Sablan Family Residence") =>
        host.WithCustomersAsync(customers => customers.RegisterAsync(
            new RegisterCustomerInput(name, CustomerClass.Residential, "Maria Sablan", "maria.sablan@example.com", "+1-670-532-0114")));

    private static Task<ServiceLocation> APremiseAsync(CustomersTestHost host, string line1 = "128 As Nieves Road") =>
        host.WithLocationsAsync(locations => locations.RegisterAsync(
            new ServiceLocationInput(
                Address.Create(line1, "Songsong", "Rota", "MP", postalCode: "96951"),
                "Single-storey house")));

    private static async Task<ServiceAccount> AnAccountAsync(CustomersTestHost host)
    {
        var customer = await ACustomerAsync(host);
        var premise = await APremiseAsync(host);

        return await host.WithAccountsAsync(accounts =>
            accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, premise.Id, "Requested at the counter")));
    }

    [Fact]
    public async Task Opening_an_account_issues_the_first_number_and_leaves_it_pending()
    {
        using var host = NewHost();

        var account = await AnAccountAsync(host);

        Assert.Equal("A-000001", account.AccountNumber);
        Assert.Equal(ServiceAccountStatus.Pending, account.Status);

        await using var database = host.NewCustomersContext();

        Assert.Equal("A-000001", (await database.ServiceAccounts.SingleAsync()).AccountNumber);
    }

    [Fact]
    public async Task Each_account_continues_the_series()
    {
        using var host = NewHost();

        await AnAccountAsync(host);

        var customer = await ACustomerAsync(host, "Taisacan Household");
        var premise = await APremiseAsync(host, "14 Tatachog Street");

        var second = await host.WithAccountsAsync(accounts =>
            accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, premise.Id)));

        Assert.Equal("A-000002", second.AccountNumber);
    }

    [Fact]
    public async Task Opening_an_account_audits_it_and_publishes_the_fact()
    {
        using var host = NewHost();

        var account = await AnAccountAsync(host);

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries.SingleAsync(candidate => candidate.EntityType == AuditEntityTypes.ServiceAccount);

        Assert.Equal(AuditActions.ServiceAccountOpened, entry.Action);
        Assert.Equal(account.Id.ToString(), entry.EntityId);
        Assert.Equal("auth0|cs-agent", entry.UserId);
        Assert.Null(entry.BeforeJson);
        Assert.Contains("A-000001", entry.AfterJson);

        var published = host.Events.Single<ServiceAccountOpened>();

        Assert.Equal(account.Id, published.ServiceAccountId);
        Assert.Equal("A-000001", published.AccountNumber);
        Assert.Equal(account.CustomerId, published.CustomerId);
        Assert.Equal(account.ServiceLocationId, published.ServiceLocationId);
        Assert.Equal(nameof(ServiceAccountStatus.Pending), published.Status);
        Assert.Equal(Now, published.OccurredAt);
    }

    [Fact]
    public async Task The_opening_history_line_names_the_agent_who_took_the_call()
    {
        using var host = NewHost();

        var account = await AnAccountAsync(host);

        await using var database = host.NewCustomersContext();

        var entry = await database.ServiceAccountHistory.SingleAsync();

        Assert.Equal(account.Id, entry.ServiceAccountId);
        Assert.Null(entry.FromStatus);
        Assert.Equal(ServiceAccountStatus.Pending, entry.ToStatus);
        Assert.Equal("Requested at the counter", entry.Reason);
        Assert.Equal("auth0|cs-agent", entry.ActorId);
        Assert.Equal("Ana Cruz", entry.ActorName);
    }

    [Fact]
    public async Task Starting_service_records_the_transition_everywhere_it_belongs()
    {
        var clock = new FakeClock(Now);
        using var host = NewHost(clock);

        var account = await AnAccountAsync(host);

        clock.Advance(TimeSpan.FromDays(3));

        var started = await host.WithAccountsAsync(accounts => accounts.StartServiceAsync(account.Id, "Connection completed"));

        Assert.Equal(ServiceAccountStatus.Active, started.Status);
        Assert.Equal(Now.AddDays(3), started.ServiceStartedAt);

        await using var database = host.NewCustomersContext();
        await using var platform = host.NewPlatformContext();

        var history = await database.ServiceAccountHistory.OrderBy(entry => entry.Id).ToListAsync();

        Assert.Equal(
            [ServiceAccountStatus.Pending, ServiceAccountStatus.Active],
            history.Select(entry => entry.ToStatus).ToArray());

        var audited = await platform.AuditEntries
            .Where(entry => entry.Action == AuditActions.ServiceAccountStarted)
            .SingleAsync();

        // Before and after are both recorded, so the trail says what changed rather than only what
        // it ended up as — invariant 1.
        Assert.Contains(nameof(ServiceAccountStatus.Pending), audited.BeforeJson);
        Assert.Contains(nameof(ServiceAccountStatus.Active), audited.AfterJson);

        var published = host.Events.Single<ServiceStarted>();

        Assert.Equal(account.Id, published.ServiceAccountId);
        Assert.Equal("Connection completed", published.Reason);
    }

    [Fact]
    public async Task Stopping_and_closing_each_publish_their_own_fact()
    {
        var clock = new FakeClock(Now);
        using var host = NewHost(clock);

        var account = await AnAccountAsync(host);

        clock.Advance(TimeSpan.FromDays(3));
        await host.WithAccountsAsync(accounts => accounts.StartServiceAsync(account.Id, null));

        clock.Advance(TimeSpan.FromDays(90));
        await host.WithAccountsAsync(accounts => accounts.StopServiceAsync(account.Id, "Disconnected for non-payment"));

        clock.Advance(TimeSpan.FromDays(30));
        var closed = await host.WithAccountsAsync(accounts => accounts.CloseAsync(account.Id, "Customer moved out"));

        Assert.Equal(ServiceAccountStatus.Closed, closed.Status);

        Assert.Equal("Disconnected for non-payment", host.Events.Single<ServiceStopped>().Reason);
        Assert.Equal("Customer moved out", host.Events.Single<ServiceAccountClosed>().Reason);
    }

    [Fact]
    public async Task The_history_endpoint_returns_every_line_oldest_first()
    {
        var clock = new FakeClock(Now);
        using var host = NewHost(clock);

        var account = await AnAccountAsync(host);

        clock.Advance(TimeSpan.FromDays(3));
        await host.WithAccountsAsync(accounts => accounts.StartServiceAsync(account.Id, "Connection completed"));

        clock.Advance(TimeSpan.FromDays(90));
        await host.WithAccountsAsync(accounts => accounts.StopServiceAsync(account.Id, "Disconnected for non-payment"));

        var history = await host.WithAccountsAsync(accounts => accounts.HistoryAsync(account.Id));

        Assert.Equal(
            [ServiceAccountStatus.Pending, ServiceAccountStatus.Active, ServiceAccountStatus.Disconnected],
            history.Select(entry => entry.ToStatus).ToArray());
    }

    [Fact]
    public async Task An_illegal_transition_is_refused_and_changes_nothing()
    {
        // The failure path an operator actually hits: "stop service" on an account that was never
        // connected. A 409 from the aggregate, and the account is exactly as it was.
        using var host = NewHost();

        var account = await AnAccountAsync(host);

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithAccountsAsync(accounts => accounts.StopServiceAsync(account.Id, "Cut it off")));

        await using var database = host.NewCustomersContext();

        Assert.Equal(ServiceAccountStatus.Pending, (await database.ServiceAccounts.SingleAsync()).Status);

        // Nothing half-applied: the rolled-back transaction took the history line, the audit entry
        // and the event with it.
        Assert.Single(await database.ServiceAccountHistory.ToListAsync());
        Assert.Empty(host.Events.Published.OfType<ServiceStopped>());
    }

    [Fact]
    public async Task A_premise_cannot_be_served_by_two_open_accounts()
    {
        using var host = NewHost();

        var first = await ACustomerAsync(host);
        var premise = await APremiseAsync(host);

        await host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(first.Id, premise.Id)));

        var second = await ACustomerAsync(host, "Taisacan Household");

        var failure = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(second.Id, premise.Id))));

        Assert.Contains("A-000001", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_an_account_frees_its_premise_for_the_next_occupant()
    {
        var clock = new FakeClock(Now);
        using var host = NewHost(clock);

        var outgoing = await ACustomerAsync(host);
        var premise = await APremiseAsync(host);

        var first = await host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(outgoing.Id, premise.Id)));

        clock.Advance(TimeSpan.FromDays(1));
        await host.WithAccountsAsync(accounts => accounts.CloseAsync(first.Id, "Tenant moved out"));

        clock.Advance(TimeSpan.FromDays(1));
        var incoming = await ACustomerAsync(host, "Taisacan Household");

        var second = await host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(incoming.Id, premise.Id)));

        Assert.Equal("A-000002", second.AccountNumber);
        Assert.Equal(ServiceAccountStatus.Pending, second.Status);
    }

    [Fact]
    public async Task A_disconnected_account_still_holds_its_premise()
    {
        // The reason the uniqueness rule excludes only Closed and not Disconnected: a cut supply is
        // reconnectable, so connecting somebody else there would strand the first account.
        var clock = new FakeClock(Now);
        using var host = NewHost(clock);

        var customer = await ACustomerAsync(host);
        var premise = await APremiseAsync(host);

        var account = await host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, premise.Id)));

        clock.Advance(TimeSpan.FromDays(1));
        await host.WithAccountsAsync(accounts => accounts.StartServiceAsync(account.Id, null));

        clock.Advance(TimeSpan.FromDays(1));
        await host.WithAccountsAsync(accounts => accounts.StopServiceAsync(account.Id, "Disconnected for non-payment"));

        var other = await ACustomerAsync(host, "Taisacan Household");

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(other.Id, premise.Id))));
    }

    [Fact]
    public async Task A_customer_may_hold_accounts_at_several_premises()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host);
        var first = await APremiseAsync(host);
        var second = await APremiseAsync(host, "14 Tatachog Street");

        await host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, first.Id)));
        await host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, second.Id)));

        var held = await host.WithAccountsAsync(accounts => accounts.ListAsync(new ServiceAccountQuery(CustomerId: customer.Id)));

        Assert.Equal(2, held.Count);
    }

    [Fact]
    public async Task A_suspended_customer_cannot_take_on_new_service()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host);
        var premise = await APremiseAsync(host);

        await host.WithCustomersAsync(customers => customers.ChangeStatusAsync(customer.Id, CustomerStatus.Active, null));
        await host.WithCustomersAsync(customers => customers.ChangeStatusAsync(customer.Id, CustomerStatus.Suspended, "Unpaid balance"));

        var failure = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, premise.Id))));

        Assert.Contains("Suspended", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deactivated_premise_cannot_be_connected()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host);
        var premise = await APremiseAsync(host);

        await host.WithLocationsAsync(locations => locations.UpdateAsync(
            premise.Id,
            new ServiceLocationInput(premise.Address, premise.Description, IsActive: false, "Demolished")));

        var failure = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, premise.Id))));

        Assert.Contains("deactivated", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_cannot_be_opened_for_a_customer_who_is_not_there()
    {
        using var host = NewHost();

        var premise = await APremiseAsync(host);

        await Assert.ThrowsAsync<CustomerNotFoundException>(() =>
            host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(Guid.CreateVersion7(Now), premise.Id))));
    }

    [Fact]
    public async Task An_account_cannot_be_opened_at_a_premise_that_is_not_there()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host);

        await Assert.ThrowsAsync<ServiceLocationNotFoundException>(() =>
            host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, Guid.CreateVersion7(Now)))));
    }

    [Fact]
    public async Task Reading_the_history_of_an_account_that_is_not_there_is_a_404_not_an_empty_list()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<ServiceAccountNotFoundException>(() =>
            host.WithAccountsAsync(accounts => accounts.HistoryAsync(Guid.CreateVersion7(Now))));
    }

    [Fact]
    public async Task Opening_an_account_does_not_move_the_customers_own_status()
    {
        // Deliberate: a prospect becoming a customer is somebody's decision, and a side effect here
        // would move a status nobody asked to move.
        using var host = NewHost();

        var customer = await ACustomerAsync(host);
        var premise = await APremiseAsync(host);

        await host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, premise.Id)));

        await using var database = host.NewCustomersContext();

        Assert.Equal(CustomerStatus.Prospect, (await database.Customers.SingleAsync()).Status);
    }

    [Fact]
    public async Task The_list_filters_on_status_and_on_premise()
    {
        var clock = new FakeClock(Now);
        using var host = NewHost(clock);

        var customer = await ACustomerAsync(host);
        var first = await APremiseAsync(host);
        var second = await APremiseAsync(host, "14 Tatachog Street");

        var live = await host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, first.Id)));
        await host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, second.Id)));

        clock.Advance(TimeSpan.FromDays(1));
        await host.WithAccountsAsync(accounts => accounts.StartServiceAsync(live.Id, null));

        var active = await host.WithAccountsAsync(accounts => accounts.ListAsync(new ServiceAccountQuery(Status: ServiceAccountStatus.Active)));
        var atSecond = await host.WithAccountsAsync(accounts => accounts.ListAsync(new ServiceAccountQuery(ServiceLocationId: second.Id)));

        Assert.Equal(live.Id, Assert.Single(active).Id);
        Assert.Equal(second.Id, Assert.Single(atSecond).ServiceLocationId);
    }

    [Fact]
    public async Task The_list_finds_an_account_by_part_of_its_number_whatever_the_casing()
    {
        using var host = NewHost();

        var account = await AnAccountAsync(host);

        var found = await host.WithAccountsAsync(accounts => accounts.ListAsync(new ServiceAccountQuery("a-0000")));

        Assert.Equal(account.Id, Assert.Single(found).Id);
    }
}
