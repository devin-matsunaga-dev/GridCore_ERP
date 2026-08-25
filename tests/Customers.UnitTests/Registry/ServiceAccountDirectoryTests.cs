using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.UnitTests.Infrastructure;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>
/// The service account registry as the rest of GridCore reads it — GridCore's second cross-module
/// read seam, and the one Billing (WP-2.3) raises every bill through.
/// </summary>
public class ServiceAccountDirectoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static CustomersTestHost NewHost() => new(new FakeClock(Now), new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    private static async Task<ServiceAccount> AnAccountAsync(
        CustomersTestHost host,
        string name = "Sablan Family Residence",
        string line1 = "128 As Nieves Road")
    {
        var customer = await host.WithCustomersAsync(customers => customers.RegisterAsync(
            new RegisterCustomerInput(name, CustomerClass.Residential, "Maria Sablan", null, null, 0m)));

        var premise = await host.WithLocationsAsync(locations => locations.RegisterAsync(
            new ServiceLocationInput(
                Address.Create(line1, "Songsong", "Rota", "MP", postalCode: "96951"),
                "Single-storey house")));

        return await host.WithAccountsAsync(accounts =>
            accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, premise.Id, "Requested at the counter")));
    }

    [Fact]
    public async Task An_account_is_summarised_with_the_customer_that_holds_it()
    {
        // The join is here rather than left to the caller: a bill header names the customer, and a
        // caller that had to fetch it separately would be a caller asking for a customer directory
        // it does not need.
        using var host = NewHost();

        var account = await AnAccountAsync(host);

        var summary = await host.WithAccountDirectoryAsync(directory => directory.FindAsync(account.Id));

        Assert.NotNull(summary);
        Assert.Equal(account.AccountNumber, summary.AccountNumber);
        Assert.Equal(account.CustomerId, summary.CustomerId);
        Assert.Equal("Sablan Family Residence", summary.CustomerName);
        Assert.Equal(account.ServiceLocationId, summary.ServiceLocationId);
    }

    [Fact]
    public async Task A_status_crosses_the_boundary_by_name_not_as_an_enum()
    {
        // Contracts takes no dependency on this module's types, so the status travels as a string —
        // the same call WP-1.1's events made about the customer class.
        using var host = NewHost();

        var account = await AnAccountAsync(host);

        var summary = await host.WithAccountDirectoryAsync(directory => directory.FindAsync(account.Id));

        Assert.Equal(nameof(ServiceAccountStatus.Pending), summary!.Status);
    }

    [Fact]
    public async Task An_account_that_never_started_reports_no_service_start()
    {
        // What Billing gates on: an account opened but never energised consumed nothing under its
        // own name, so the units on the meter at its premise are not its units.
        using var host = NewHost();

        var account = await AnAccountAsync(host);

        var pending = await host.WithAccountDirectoryAsync(directory => directory.FindAsync(account.Id));

        Assert.Null(pending!.ServiceStartedAt);

        await host.WithAccountsAsync(accounts => accounts.StartServiceAsync(account.Id, "Connected."));

        var started = await host.WithAccountDirectoryAsync(directory => directory.FindAsync(account.Id));

        Assert.NotNull(started!.ServiceStartedAt);
        Assert.Equal(nameof(ServiceAccountStatus.Active), started.Status);
    }

    [Fact]
    public async Task An_id_that_matches_nothing_is_null_rather_than_a_throw()
    {
        using var host = NewHost();

        Assert.Null(await host.WithAccountDirectoryAsync(directory => directory.FindAsync(Guid.CreateVersion7())));
    }

    [Fact]
    public async Task The_open_account_at_a_premise_is_the_one_that_still_holds_it()
    {
        // The derivation WP-2.1 named but did not have to make: a meter is fitted to a premise, so
        // the account a bill is raised against is "the account open at the premise this meter is
        // on". ux_service_accounts_open_location is what guarantees there is at most one.
        using var host = NewHost();

        var account = await AnAccountAsync(host);

        var found = await host.WithAccountDirectoryAsync(directory =>
            directory.FindOpenAtLocationAsync(account.ServiceLocationId));

        Assert.Equal(account.Id, found!.Id);
        Assert.True(found.HoldsPremise);
    }

    [Fact]
    public async Task A_closed_account_has_released_its_premise()
    {
        // Closing is what frees a premise for the next occupant (WP-1.2), so a closed account is not
        // "the account here" and nothing is billed at that premise until somebody opens another.
        using var host = NewHost();

        var account = await AnAccountAsync(host);

        await host.WithAccountsAsync(accounts => accounts.CloseAsync(account.Id, "Tenant moved out."));

        Assert.Null(await host.WithAccountDirectoryAsync(directory =>
            directory.FindOpenAtLocationAsync(account.ServiceLocationId)));

        // The account itself is still there and still readable by id — it is the premise it has let
        // go of, not its own record.
        var byId = await host.WithAccountDirectoryAsync(directory => directory.FindAsync(account.Id));

        Assert.False(byId!.HoldsPremise);
        Assert.Equal(nameof(ServiceAccountStatus.Closed), byId.Status);
    }

    [Fact]
    public async Task A_disconnected_account_still_holds_its_premise()
    {
        // A disconnection leaves a balance and a premise allocated; only a closure is final. That is
        // what lets Billing raise a bill for what was used before the supply was cut.
        using var host = NewHost();

        var account = await AnAccountAsync(host);

        await host.WithAccountsAsync(accounts => accounts.StartServiceAsync(account.Id, "Connected."));
        await host.WithAccountsAsync(accounts => accounts.StopServiceAsync(account.Id, "Unpaid balance."));

        var found = await host.WithAccountDirectoryAsync(directory =>
            directory.FindOpenAtLocationAsync(account.ServiceLocationId));

        Assert.Equal(account.Id, found!.Id);
        Assert.Equal(nameof(ServiceAccountStatus.Disconnected), found.Status);

        // And it still reports when supply was last energised, which is what a final bill measures
        // from.
        Assert.NotNull(found.ServiceStartedAt);
    }

    [Fact]
    public async Task A_premise_nobody_takes_service_at_resolves_to_nothing()
    {
        // WP-2.1 seeds this case on purpose: a new build can be metered before anybody is billed
        // there, because metering and billing are separate questions.
        using var host = NewHost();

        Assert.Null(await host.WithAccountDirectoryAsync(directory =>
            directory.FindOpenAtLocationAsync(Guid.CreateVersion7())));
    }

    [Fact]
    public async Task Open_accounts_are_batched_by_premise_for_a_billing_run()
    {
        // Keyed by PREMISE, not by account: the caller holds a meter's premise and is asking who is
        // being served there. One boundary call per run rather than one per meter.
        using var host = NewHost();

        var first = await AnAccountAsync(host);
        var second = await AnAccountAsync(host, "Camacho Store", "9 Chalan Kanoa Street");

        var found = await host.WithAccountDirectoryAsync(directory => directory.FindOpenAtLocationsAsync(
        [
            first.ServiceLocationId,
            second.ServiceLocationId,
            Guid.CreateVersion7(),
        ]));

        Assert.Equal(2, found.Count);
        Assert.Equal(first.Id, found[first.ServiceLocationId].Id);
        Assert.Equal(second.Id, found[second.ServiceLocationId].Id);
    }

    [Fact]
    public async Task A_batch_lookup_of_nothing_asks_the_database_nothing()
    {
        using var host = NewHost();

        Assert.Empty(await host.WithAccountDirectoryAsync(directory => directory.FindManyAsync([])));
        Assert.Empty(await host.WithAccountDirectoryAsync(directory => directory.FindOpenAtLocationsAsync([])));
    }

    [Fact]
    public async Task Ids_that_match_nothing_are_absent_rather_than_failing_the_batch()
    {
        // A caller rendering a list has to cope with a row it cannot resolve anyway, and throwing
        // would make one bad id lose the whole page — the call WP-2.1's premise directory made.
        using var host = NewHost();

        var account = await AnAccountAsync(host);

        var found = await host.WithAccountDirectoryAsync(directory =>
            directory.FindManyAsync([account.Id, Guid.CreateVersion7()]));

        Assert.Equal(account.Id, Assert.Single(found).Key);
    }

    [Fact]
    public async Task The_same_id_asked_for_twice_is_answered_once()
    {
        using var host = NewHost();

        var account = await AnAccountAsync(host);

        var found = await host.WithAccountDirectoryAsync(directory =>
            directory.FindManyAsync([account.Id, account.Id]));

        Assert.Single(found);
    }
}
