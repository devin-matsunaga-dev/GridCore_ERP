using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.UnitTests.Deposits;

/// <summary>
/// WP-2.17's re-assessment: what a customer is holding against what the schedule asks of them
/// today, across every open account they hold.
/// </summary>
/// <remarks>
/// The measured input comes through <see cref="FakeUsageDirectory"/> — Metering's seam — and never
/// from the caller, which is the point of the package: a caller that supplied the usage could supply
/// a different number and get a different deposit.
/// </remarks>
public sealed class DepositReassessmentServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly CustomersTestHost _host = new(new FakeClock(Now), new FakeCurrentUser("auth0|rep", "A service rep"));
    private int _ordinal;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_customer_taking_no_service_is_asked_for_nothing()
    {
        // A prospect on the books with no account. The schedule is keyed on the SUPPLY since
        // WP-2.17, so there is nothing to key on and nothing to ask for — a figure here would be the
        // utility chasing a deposit for a connection nobody has applied for.
        var customer = await ACustomerAsync();

        var requirement = await ReassessAsync(customer.Id);

        Assert.Empty(requirement.Accounts);
        Assert.Equal(Money.Zero, requirement.RequiredAmount);
        Assert.Equal(Money.Zero, requirement.ShortfallAmount);
        Assert.True(requirement.IsCovered);
    }

    [Fact]
    public async Task A_premise_taking_three_supplies_is_assessed_three_times()
    {
        // The shape the package exists for. One house, three accounts, three rules — and a required
        // figure that is their sum rather than whichever one a class-keyed schedule happened to name.
        var customer = await ACustomerAsync();
        var premise = await APremiseAsync();

        await OpenAsync(customer.Id, premise.Id, ServiceType.Electricity);
        await OpenAsync(customer.Id, premise.Id, ServiceType.Water);
        await OpenAsync(customer.Id, premise.Id, ServiceType.Wastewater);

        var requirement = await ReassessAsync(customer.Id);

        // In service order, which is how a rep reads "what is taken here".
        Assert.Equal(
            [ServiceType.Electricity, ServiceType.Water, ServiceType.Wastewater],
            requirement.Accounts.Select(line => line.Assessment.ServiceType));

        // $75 electric + $50 water + $30 wastewater, all off their published floors.
        Assert.Equal(155.00m, requirement.RequiredAmount);
        Assert.Equal(155.00m, requirement.ShortfallAmount);
    }

    [Fact]
    public async Task A_measured_premise_is_assessed_on_what_it_actually_uses()
    {
        var customer = await ACustomerAsync();
        var premise = await APremiseAsync();

        await OpenAsync(customer.Id, premise.Id, ServiceType.Electricity);

        // 400 kWh a month at $0.3200 over two months is $256.00, which clears the $75 floor.
        _host.Usage.Measured(premise.Id, 400m);

        var line = Assert.Single((await ReassessAsync(customer.Id)).Accounts);

        Assert.True(line.HasUsageHistory);
        Assert.True(line.Assessment.IsUsageBased);
        Assert.Equal(400m, line.Assessment.AverageMonthlyUsage);
        Assert.Equal(256.00m, line.Assessment.Amount);
    }

    [Fact]
    public async Task A_light_user_is_asked_for_the_published_minimum_rather_than_their_usage()
    {
        var customer = await ACustomerAsync();
        var premise = await APremiseAsync();

        await OpenAsync(customer.Id, premise.Id, ServiceType.Electricity);

        // 20 kWh a month works out to $12.80 over two months. The minimum is a floor, never a
        // ceiling, so the floor answers.
        _host.Usage.Measured(premise.Id, 20m);

        var line = Assert.Single((await ReassessAsync(customer.Id)).Accounts);

        Assert.Equal(75.00m, line.Assessment.Amount);
        Assert.False(line.Assessment.IsUsageBased);

        // Measured, but not what decided the figure — two different facts, and a rep explaining the
        // deposit needs both.
        Assert.True(line.HasUsageHistory);
    }

    [Fact]
    public async Task A_premise_with_no_reading_history_falls_back_to_the_minimum()
    {
        // WORK_PACKAGES.md's rule stated end to end: no history is not zero usage, and a customer
        // nobody has metered yet is asked for the published floor rather than for nothing.
        var customer = await ACustomerAsync();
        var premise = await APremiseAsync();

        await OpenAsync(customer.Id, premise.Id, ServiceType.Electricity);

        var line = Assert.Single((await ReassessAsync(customer.Id)).Accounts);

        Assert.False(line.HasUsageHistory);
        Assert.False(line.Assessment.IsUsageBased);
        Assert.Equal(75.00m, line.Assessment.Amount);
    }

    [Fact]
    public async Task An_unmetered_account_never_asks_the_usage_register_anything()
    {
        // There is nothing to average, so the boundary call would be one whose answer is known in
        // advance. The seam is the assertion.
        var customer = await ACustomerAsync();
        var premise = await APremiseAsync();

        await OpenAsync(customer.Id, premise.Id, ServiceType.Wastewater);

        var requirement = await ReassessAsync(customer.Id);

        Assert.Equal(30.00m, requirement.RequiredAmount);
        Assert.Empty(_host.Usage.Lookups);
    }

    [Fact]
    public async Task The_usage_register_is_asked_for_the_months_the_rule_itself_takes()
    {
        // Asking a fixed window would make the answer disagree with the rule that quoted it.
        var customer = await ACustomerAsync();
        var premise = await APremiseAsync();

        await OpenAsync(customer.Id, premise.Id, ServiceType.Electricity);

        await ReassessAsync(customer.Id);

        Assert.Equal(DepositRules.UsageMonths, Assert.Single(_host.Usage.RequestedPeriods));
    }

    [Fact]
    public async Task Holding_more_than_the_schedule_asks_for_is_a_shortfall_of_zero_and_never_a_negative()
    {
        // A customer holding more than the schedule now asks for is not owed a refund by arithmetic:
        // giving a deposit back is a decision somebody makes, and a negative shortfall on a screen
        // would read as the utility announcing one.
        var customer = await ACustomerAsync();
        var premise = await APremiseAsync();

        await OpenAsync(customer.Id, premise.Id, ServiceType.Electricity);

        await _host.WithDepositsAsync(deposits =>
            deposits.CollectAsync(customer.Id, new CollectDepositInput(200.00m, Reason: "Taken at the counter.")));

        var requirement = await ReassessAsync(customer.Id);

        Assert.Equal(200.00m, requirement.HeldAmount);
        Assert.Equal(75.00m, requirement.RequiredAmount);
        Assert.Equal(Money.Zero, requirement.ShortfallAmount);
        Assert.True(requirement.IsCovered);
    }

    [Fact]
    public async Task Part_of_what_is_asked_for_leaves_the_difference_as_the_shortfall()
    {
        var customer = await ACustomerAsync();
        var premise = await APremiseAsync();

        await OpenAsync(customer.Id, premise.Id, ServiceType.Electricity);

        await _host.WithDepositsAsync(deposits =>
            deposits.CollectAsync(customer.Id, new CollectDepositInput(25.00m, Reason: "Part payment at the counter.")));

        var requirement = await ReassessAsync(customer.Id);

        Assert.Equal(50.00m, requirement.ShortfallAmount);
        Assert.False(requirement.IsCovered);
    }

    [Fact]
    public async Task A_closed_account_contributes_nothing()
    {
        // The utility is no longer exposed on a supply it has stopped delivering.
        var customer = await ACustomerAsync();
        var premise = await APremiseAsync();

        var account = await OpenAsync(customer.Id, premise.Id, ServiceType.Electricity);

        await _host.WithAccountsAsync(accounts => accounts.CloseAsync(account.Id, "Moved off island."));

        var requirement = await ReassessAsync(customer.Id);

        Assert.Empty(requirement.Accounts);
        Assert.Equal(Money.Zero, requirement.RequiredAmount);
    }

    [Fact]
    public async Task A_pending_account_counts_because_the_deposit_comes_before_the_supply()
    {
        // Leaving a pending account out would quote a shortfall of zero to exactly the customer who
        // has not paid a deposit yet — which is the whole reason a deposit is asked for.
        var customer = await ACustomerAsync();
        var premise = await APremiseAsync();

        var account = await OpenAsync(customer.Id, premise.Id, ServiceType.Electricity);

        Assert.Equal(ServiceAccountStatus.Pending, account.Status);

        var requirement = await ReassessAsync(customer.Id);

        Assert.Equal(75.00m, requirement.RequiredAmount);
    }

    [Fact]
    public async Task A_commercial_customer_is_assessed_on_the_commercial_rules()
    {
        var customer = await ACustomerAsync(CustomerClass.Commercial);
        var premise = await APremiseAsync();

        await OpenAsync(customer.Id, premise.Id, ServiceType.Electricity);

        var requirement = await ReassessAsync(customer.Id);

        Assert.Equal(CustomerClass.Commercial, requirement.CustomerClass);
        Assert.Equal(450.00m, requirement.RequiredAmount);
    }

    [Fact]
    public async Task Re_assessing_a_customer_who_does_not_exist_is_a_not_found() =>
        await Assert.ThrowsAsync<CustomerNotFoundException>(() => ReassessAsync(Guid.CreateVersion7(Now)));

    private Task<DepositRequirement> ReassessAsync(Guid customerId) =>
        _host.WithReassessmentAsync(reassessment => reassessment.ReassessAsync(customerId));

    private Task<Customer> ACustomerAsync(CustomerClass customerClass = CustomerClass.Residential) =>
        _host.WithCustomersAsync(customers =>
            customers.RegisterAsync(new RegisterCustomerInput($"Sablan Residence {++_ordinal}", customerClass)));

    private Task<ServiceLocation> APremiseAsync() =>
        _host.WithLocationsAsync(locations => locations.RegisterAsync(
            new ServiceLocationInput(
                Address.Create($"{++_ordinal} As Nieves Road", "Songsong", "Rota", "MP"),
                "Meter on the north wall")));

    private Task<ServiceAccount> OpenAsync(Guid customerId, Guid premiseId, ServiceType serviceType) =>
        _host.WithAccountsAsync(accounts =>
            accounts.OpenAsync(new OpenServiceAccountInput(customerId, premiseId, serviceType)));
}
