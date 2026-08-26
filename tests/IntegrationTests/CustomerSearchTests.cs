using GridCore.IntegrationTests.Infrastructure;
using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Search;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Metering.Features.Meters;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// The CSR search box against real Postgres and the shipped composition (WP-2.9).
/// </summary>
/// <remarks>
/// <para>
/// The fast tier proves the rules — classification, normalisation, ranking, paging — on SQLite, and
/// it proves them well. What only a container can show is that the <b>query</b> means the same thing
/// on the database the product actually runs on: <c>lower()</c> over a <c>citext</c>-less column, a
/// chain of <c>replace()</c> nested seven deep, and a three-way join whose <c>Where</c> sits on the
/// entities rather than the projection. Every one of those is a place where Npgsql and the SQLite
/// provider could legitimately disagree, and the fast tier would never see it.
/// </para>
/// <para>
/// It is also the only tier where the meter hop is real. In the fast tier
/// <c>IMeterDirectory</c> is a double; here it is Metering's own implementation over the
/// <c>metering</c> schema, so a meter number really does cross a module boundary and come back as a
/// customer in the <c>customers</c> schema.
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CustomerSearchTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Customer> ACustomerAsync(string name, string? phone = null)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ICustomerService>()
            .RegisterAsync(new RegisterCustomerInput(name, CustomerClass.Residential, null, null, phone));
    }

    private async Task<ServiceLocation> APremiseAsync(string line1)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IServiceLocationService>()
            .RegisterAsync(new ServiceLocationInput(
                Address.Create(line1, "Songsong", "Rota", "MP", postalCode: "96951"),
                "Meter on the north wall"));
    }

    private async Task<ServiceAccount> ServedAsync(Customer customer, ServiceLocation premise)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IServiceAccountService>()
            .OpenAsync(new OpenServiceAccountInput(customer.Id, premise.Id, ServiceType.Electricity, "Requested at the counter"));
    }

    private async Task<string> AMeterAtAsync(string serialNumber, Guid premise)
    {
        await using var scope = fixture.CreateScope();

        var meters = scope.ServiceProvider.GetRequiredService<IMeterService>();

        var registered = await meters.RegisterAsync(new RegisterMeterInput(serialNumber, MeterType.SinglePhase));
        var fitted = await meters.AssignAsync(registered.Meter.Id, new AssignMeterInput(premise, 0m));

        return fitted.Meter.MeterNumber;
    }

    private async Task<CustomerSearchResult> SearchAsync(string term, int page = 1, int pageSize = 20)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ICustomerSearchService>()
            .SearchAsync(new CustomerSearchQuery(term, Page: page, PageSize: pageSize));
    }

    [Fact]
    public async Task An_account_number_is_matched_case_insensitively_by_Postgres()
    {
        // LIKE is case-sensitive on Postgres and ILIKE is Npgsql-only, so the query lower-cases both
        // sides (WP-1.1's rule). This is the tier where that decision is actually exercised.
        var customer = await ACustomerAsync("Sablan Family Residence");

        var result = await SearchAsync(customer.AccountNumber.ToLowerInvariant());

        var hit = Assert.Single(result.Hits);

        Assert.Equal(customer.Id, hit.Customer.Id);
        Assert.True(hit.IsExact);
    }

    [Fact]
    public async Task Punctuation_comes_off_a_stored_telephone_number_inside_Postgres()
    {
        // Seven nested replace() calls. The fast tier proves SQLite does it; nothing before this
        // proves Npgsql translates the same chain to the same answer.
        var customer = await ACustomerAsync("Taitano Hardware", "+1 (670) 285-1234");

        var hit = Assert.Single((await SearchAsync("6702851234")).Hits);

        Assert.Equal(customer.Id, hit.Customer.Id);
        Assert.Equal(CustomerMatchKind.Phone, hit.MatchedOn);
    }

    [Fact]
    public async Task An_address_candidate_query_joins_three_tables_and_survives_translation()
    {
        // The join EF cannot translate if the Where is applied to the projection instead of to the
        // entities — it compiles either way and throws only at run time, against a real provider.
        var customer = await ACustomerAsync("Sablan Family Residence");
        var premise = await APremiseAsync("12 Beach St");
        await ServedAsync(customer, premise);

        var hit = Assert.Single((await SearchAsync("12 Beach Street")).Hits);

        Assert.Equal(customer.Id, hit.Customer.Id);
        Assert.Equal(CustomerMatchKind.Address, hit.MatchedOn);
        Assert.Equal(premise.Address.OneLine, hit.ServiceAddress);
    }

    [Fact]
    public async Task A_meter_number_crosses_the_module_boundary_and_comes_back_a_customer()
    {
        // Two schemas, two modules, one seam. Customers named no metering table and Metering named no
        // customers table; the resolution happened in the middle, through Contracts.
        var customer = await ACustomerAsync("Manglona Store");
        var premise = await APremiseAsync("45 As Matuis Rd");
        var account = await ServedAsync(customer, premise);

        var meterNumber = await AMeterAtAsync("SEN-2901447", premise.Id);

        var hit = Assert.Single((await SearchAsync(meterNumber)).Hits);

        Assert.Equal(customer.Id, hit.Customer.Id);
        Assert.Equal(CustomerMatchKind.MeterNumber, hit.MatchedOn);
        Assert.Equal(meterNumber, hit.MeterNumber);
        Assert.Equal(account.AccountNumber, hit.ServiceAccountNumber);
    }

    [Fact]
    public async Task Paging_over_the_ranked_list_shows_every_customer_exactly_once()
    {
        // The order has to be total for this to hold, and "total" is a property of the comparison
        // rather than of the database — but a database free to return rows in any order is what would
        // expose it if the comparison were not.
        foreach (var index in Enumerable.Range(1, 5))
        {
            await ACustomerAsync($"Cruz Household {index:D2}");
        }

        var first = await SearchAsync("Cruz", page: 1, pageSize: 2);
        var second = await SearchAsync("Cruz", page: 2, pageSize: 2);
        var third = await SearchAsync("Cruz", page: 3, pageSize: 2);

        Assert.Equal(5, first.Total);

        Assert.Equal(
            5,
            first.Hits.Concat(second.Hits).Concat(third.Hits).Select(hit => hit.Customer.Id).Distinct().Count());
    }

    [Fact]
    public async Task The_registry_filters_narrow_a_match_that_arrived_through_a_join()
    {
        // The filters reach the address and meter kinds by narrowing the customers the three-way
        // join joins against — a different SQL shape from narrowing the customer table on its own,
        // and the tier where that shape is exercised for real.
        var residential = await ACustomerAsync("Sablan Family Residence");
        await ServedAsync(residential, await APremiseAsync("12 Beach St"));

        var commercial = await ACustomerAsync("Manglona Store");
        await ServedAsync(commercial, await APremiseAsync("14 Beach St"));

        await using var scope = fixture.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICustomerSearchService>()
            .SearchAsync(new CustomerSearchQuery("Beach St", Status: CustomerStatus.Prospect));

        // Both are Prospect until service starts, so the filter keeps both — what matters here is
        // that the query translated and the join still found them.
        Assert.Equal(2, result.Total);

        var refused = await scope.ServiceProvider.GetRequiredService<ICustomerSearchService>()
            .SearchAsync(new CustomerSearchQuery("Beach St", Status: CustomerStatus.Closed));

        Assert.Empty(refused.Hits);
    }

    [Fact]
    public async Task A_term_that_matches_nothing_is_an_empty_page_and_not_a_fault()
    {
        await ACustomerAsync("Sablan Family Residence");

        var result = await SearchAsync("Nobody At All");

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.Total);
        Assert.False(result.Truncated);
    }
}
