using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Transitions;
using GridCore.Modules.Customers.Features.Search;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.UnitTests.Infrastructure;

namespace GridCore.Modules.Customers.UnitTests.Search;

/// <summary>
/// The search box end to end over the module's own tables — real EF, real SQL, no containers. That
/// is what makes the two halves of the phone comparison testable together: <c>SearchText.Digits</c>
/// in C# and the <c>Replace</c> chain the candidate query is built from have to agree, and reasoning
/// about two lists of punctuation is not the same as running them.
/// </summary>
public class CustomerSearchServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private static CustomersTestHost NewHost() => new(new FakeClock(Now), new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    private static Task<Customer> ACustomerAsync(
        CustomersTestHost host,
        string name,
        string? phone = null,
        CustomerClass customerClass = CustomerClass.Residential) =>
        host.WithCustomersAsync(customers => customers.RegisterAsync(
            new RegisterCustomerInput(name, customerClass, null, null, phone)));

    private static Task<ServiceLocation> APremiseAsync(CustomersTestHost host, string line1, string city = "Songsong") =>
        host.WithLocationsAsync(locations => locations.RegisterAsync(
            new ServiceLocationInput(Address.Create(line1, city, "Rota", "MP", postalCode: "96951"), null)));

    private static Task<ServiceAccount> ServedAsync(CustomersTestHost host, Customer customer, ServiceLocation premise) =>
        host.WithAccountsAsync(accounts =>
            accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, premise.Id, ServiceType.Electricity, "Requested at the counter")));

    private static Task<CustomerSearchResult> SearchAsync(CustomersTestHost host, string? term, int page = 1, int pageSize = 20) =>
        host.WithSearchAsync(search => search.SearchAsync(new CustomerSearchQuery(term, Page: page, PageSize: pageSize)));

    private static Task<CustomerSearchResult> FilteredAsync(
        CustomersTestHost host,
        string term,
        CustomerStatus? status = null,
        CustomerClass? customerClass = null) =>
        host.WithSearchAsync(search => search.SearchAsync(new CustomerSearchQuery(term, status, customerClass)));

    [Fact]
    public async Task An_exact_account_number_finds_the_customer_and_says_so()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Sablan Family Residence");

        var result = await SearchAsync(host, customer.AccountNumber);

        var hit = Assert.Single(result.Hits);

        Assert.Equal(customer.Id, hit.Customer.Id);
        Assert.Equal(CustomerMatchKind.AccountNumber, hit.MatchedOn);
        Assert.True(hit.IsExact);
        Assert.Equal(customer.AccountNumber, hit.MatchedValue);
    }

    [Fact]
    public async Task An_account_number_is_matched_without_regard_to_case()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Sablan Family Residence");

        var result = await SearchAsync(host, customer.AccountNumber.ToLowerInvariant());

        Assert.Equal(customer.Id, Assert.Single(result.Hits).Customer.Id);
    }

    [Fact]
    public async Task An_exact_account_number_hit_ranks_above_everything_else()
    {
        using var host = NewHost();

        // Two customers, and a term that is one's account number and part of the other's. The exact
        // identifier wins whatever else the term happens to touch.
        var quoted = await ACustomerAsync(host, "Sablan Family Residence");
        await ACustomerAsync(host, $"Trading company {quoted.AccountNumber} Ltd", customerClass: CustomerClass.Commercial);

        var result = await SearchAsync(host, quoted.AccountNumber);

        Assert.Equal(quoted.Id, result.Hits[0].Customer.Id);
        Assert.Equal(CustomerMatchKind.AccountNumber, result.Hits[0].MatchedOn);
        Assert.True(result.Hits[0].IsExact);
    }

    [Fact]
    public async Task A_partial_account_number_still_finds_them()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Sablan Family Residence");

        var result = await SearchAsync(host, customer.AccountNumber[2..]);

        var hit = Assert.Single(result.Hits);

        Assert.Equal(customer.Id, hit.Customer.Id);
        Assert.False(hit.IsExact);
    }

    [Fact]
    public async Task A_name_matches_partially_and_without_regard_to_case()
    {
        using var host = NewHost();

        var sablan = await ACustomerAsync(host, "Sablan Family Residence");
        await ACustomerAsync(host, "Taitano Hardware", customerClass: CustomerClass.Commercial);

        var result = await SearchAsync(host, "sablan");

        var hit = Assert.Single(result.Hits);

        Assert.Equal(sablan.Id, hit.Customer.Id);
        Assert.Equal(CustomerMatchKind.Name, hit.MatchedOn);
        Assert.False(hit.IsExact);
    }

    [Fact]
    public async Task An_exact_name_ranks_above_a_partial_one()
    {
        using var host = NewHost();

        await ACustomerAsync(host, "Cruz Family Residence");
        var exact = await ACustomerAsync(host, "Cruz");

        var result = await SearchAsync(host, "Cruz");

        // The exact probe short-circuits the partial scan, so the partial match is not even offered:
        // somebody who typed a whole name has told you which one they mean.
        var hit = Assert.Single(result.Hits);

        Assert.Equal(exact.Id, hit.Customer.Id);
        Assert.True(hit.IsExact);
    }

    [Theory]
    [InlineData("(670) 285-1234")]
    [InlineData("670-285-1234")]
    [InlineData("670.285.1234")]
    [InlineData("6702851234")]
    [InlineData("670 285 1234")]
    public async Task A_phone_matches_however_either_side_was_punctuated(string typed)
    {
        // The half that cannot be reasoned about: the stored column keeps whatever was typed at the
        // counter and the comparison happens in SQL, so the Replace chain and SearchText.Digits have
        // to strip the same characters. Running both is the only way to know they do.
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Sablan Family Residence", phone: "(670) 285-1234");

        var result = await SearchAsync(host, typed);

        var hit = Assert.Single(result.Hits);

        Assert.Equal(customer.Id, hit.Customer.Id);
        Assert.Equal(CustomerMatchKind.Phone, hit.MatchedOn);
        Assert.True(hit.IsExact);
    }

    [Fact]
    public async Task A_stored_phone_written_any_other_way_is_still_found()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Taitano Hardware", phone: "+1 670/285.1234", customerClass: CustomerClass.Commercial);

        var result = await SearchAsync(host, "670 285 1234");

        // Partial, not exact: the stored number carries a country code the caller did not type, and
        // saying so is more honest than pretending the two figures are the same.
        var hit = Assert.Single(result.Hits);

        Assert.Equal(customer.Id, hit.Customer.Id);
        Assert.False(hit.IsExact);
    }

    [Fact]
    public async Task An_address_matches_across_an_abbreviated_street_type()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Sablan Family Residence");
        var premise = await APremiseAsync(host, "12 Beach St");
        await ServedAsync(host, customer, premise);

        var result = await SearchAsync(host, "12 Beach Street");

        var hit = Assert.Single(result.Hits);

        Assert.Equal(customer.Id, hit.Customer.Id);
        Assert.Equal(CustomerMatchKind.Address, hit.MatchedOn);
        Assert.Equal(premise.Address.OneLine, hit.ServiceAddress);
    }

    [Fact]
    public async Task An_address_typed_out_in_full_matches_the_abbreviation_the_other_way_round()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Taitano Hardware", customerClass: CustomerClass.Commercial);
        var premise = await APremiseAsync(host, "45 As Matuis Road");
        await ServedAsync(host, customer, premise);

        Assert.Equal(customer.Id, Assert.Single((await SearchAsync(host, "45 As Matuis Rd")).Hits).Customer.Id);
    }

    [Fact]
    public async Task The_narrowing_token_gets_a_candidate_to_the_second_stage_that_it_then_refuses()
    {
        // The two stages doing different jobs. "Beach" narrows to both premises in SQL; only one of
        // them contains the whole normalised address the rep typed, and the other is dropped in C#.
        using var host = NewHost();

        var twelve = await ACustomerAsync(host, "Sablan Family Residence");
        await ServedAsync(host, twelve, await APremiseAsync(host, "12 Beach St"));

        var forty = await ACustomerAsync(host, "Manglona Store", customerClass: CustomerClass.Commercial);
        await ServedAsync(host, forty, await APremiseAsync(host, "40 Beach St"));

        var result = await SearchAsync(host, "12 Beach Street");

        Assert.Equal(twelve.Id, Assert.Single(result.Hits).Customer.Id);
    }

    [Fact]
    public async Task A_meter_number_resolves_through_to_its_customer()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Sablan Family Residence");
        var premise = await APremiseAsync(host, "12 Beach St");
        var account = await ServedAsync(host, customer, premise);

        host.Meters.Fitted("MTR-000012", premise.Id);

        var result = await SearchAsync(host, "MTR-000012");

        var hit = Assert.Single(result.Hits);

        Assert.Equal(customer.Id, hit.Customer.Id);
        Assert.Equal(CustomerMatchKind.MeterNumber, hit.MatchedOn);
        Assert.True(hit.IsExact);
        Assert.Equal("MTR-000012", hit.MeterNumber);
        Assert.Equal(account.AccountNumber, hit.ServiceAccountNumber);
        Assert.Equal(premise.Address.OneLine, hit.ServiceAddress);
    }

    [Fact]
    public async Task A_meter_nobody_is_taking_service_behind_names_no_customer()
    {
        // The failure path the seam makes possible: the meter exists and is fitted, but the account
        // at that premise has been closed, so there is nobody to route the caller to. An empty
        // result, not a row with a blank name on it.
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Sablan Family Residence");
        var premise = await APremiseAsync(host, "12 Beach St");
        var account = await ServedAsync(host, customer, premise);

        await host.WithAccountsAsync(accounts => accounts.CloseAsync(account.Id, "Moved off island"));

        host.Meters.Fitted("MTR-000012", premise.Id);

        Assert.Empty((await SearchAsync(host, "MTR-000012")).Hits);
    }

    [Fact]
    public async Task A_meter_in_the_store_matches_nothing_because_it_measures_nobody()
    {
        using var host = NewHost();

        await ACustomerAsync(host, "Sablan Family Residence");
        host.Meters.InStock("MTR-000012");

        Assert.Empty((await SearchAsync(host, "MTR-000012")).Hits);
    }

    [Fact]
    public async Task An_exact_meter_number_is_probed_before_anything_is_scanned()
    {
        // The seek that keeps the fifty-times-a-day path cheap. Asserted through the seam's own call
        // counts, because "it used the index" is not something the fast tier can see.
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Sablan Family Residence");
        var premise = await APremiseAsync(host, "12 Beach St");
        await ServedAsync(host, customer, premise);

        host.Meters.Fitted("MTR-000012", premise.Id);

        await SearchAsync(host, "MTR-000012");

        Assert.Equal(1, host.Meters.ExactLookups);
        Assert.Equal(0, host.Meters.PartialLookups);
    }

    [Fact]
    public async Task A_half_remembered_meter_number_falls_back_to_the_scan()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Sablan Family Residence");
        var premise = await APremiseAsync(host, "12 Beach St");
        await ServedAsync(host, customer, premise);

        host.Meters.Fitted("MTR-000012", premise.Id);

        var result = await SearchAsync(host, "MTR-0000");

        Assert.Equal(1, host.Meters.ExactLookups);
        Assert.Equal(1, host.Meters.PartialLookups);
        Assert.False(Assert.Single(result.Hits).IsExact);
    }

    [Fact]
    public async Task A_term_that_matches_nothing_is_an_empty_result_and_not_an_error()
    {
        using var host = NewHost();

        await ACustomerAsync(host, "Sablan Family Residence");

        var result = await SearchAsync(host, "Nobody At All");

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.Total);
        Assert.Equal("Nobody At All", result.Term);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task An_empty_box_looks_for_nothing_rather_than_returning_the_whole_register()
    {
        using var host = NewHost();

        await ACustomerAsync(host, "Sablan Family Residence");

        var result = await SearchAsync(host, "   ");

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Kinds);
    }

    [Fact]
    public async Task A_page_is_cut_from_the_ranked_list_and_the_total_counts_every_page()
    {
        using var host = NewHost();

        foreach (var index in Enumerable.Range(1, 5))
        {
            await ACustomerAsync(host, $"Cruz Household {index:D2}");
        }

        var first = await SearchAsync(host, "Cruz", page: 1, pageSize: 2);
        var second = await SearchAsync(host, "Cruz", page: 2, pageSize: 2);
        var third = await SearchAsync(host, "Cruz", page: 3, pageSize: 2);

        Assert.Equal(5, first.Total);
        Assert.Equal(5, second.Total);
        Assert.Equal(2, first.Hits.Count);
        Assert.Equal(2, second.Hits.Count);
        Assert.Single(third.Hits);

        // Every customer once, across the three pages — the tie-break in the ranking doing its job.
        Assert.Equal(
            5,
            first.Hits.Concat(second.Hits).Concat(third.Hits).Select(hit => hit.Customer.Id).Distinct().Count());
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_rather_than_an_error()
    {
        using var host = NewHost();

        await ACustomerAsync(host, "Cruz Household");

        var result = await SearchAsync(host, "Cruz", page: 9, pageSize: 20);

        Assert.Empty(result.Hits);
        Assert.Equal(1, result.Total);
        Assert.Equal(9, result.Page);
    }

    [Fact]
    public async Task A_nonsense_page_is_clamped_rather_than_refused()
    {
        using var host = NewHost();

        await ACustomerAsync(host, "Cruz Household");

        var result = await SearchAsync(host, "Cruz", page: 0, pageSize: 0);

        Assert.Equal(1, result.Page);
        Assert.Equal(1, result.PageSize);
        Assert.Single(result.Hits);
    }

    [Fact]
    public async Task A_page_size_beyond_the_maximum_is_clamped_to_it()
    {
        using var host = NewHost();

        await ACustomerAsync(host, "Cruz Household");

        var result = await SearchAsync(host, "Cruz", pageSize: CustomerSearchQuery.MaxPageSize + 500);

        Assert.Equal(CustomerSearchQuery.MaxPageSize, result.PageSize);
    }

    [Fact]
    public async Task A_row_carries_the_premise_a_customer_matched_by_name_is_served_at()
    {
        // What makes three people called Cruz tellable apart. The context is fetched for the page
        // only, so it costs one bounded query however many customers matched.
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Sablan Family Residence");
        var premise = await APremiseAsync(host, "12 Beach St");
        var account = await ServedAsync(host, customer, premise);

        var hit = Assert.Single((await SearchAsync(host, "Sablan")).Hits);

        Assert.Equal(1, hit.ServiceAccountCount);
        Assert.Equal(account.AccountNumber, hit.ServiceAccountNumber);
        Assert.Equal(premise.Address.OneLine, hit.ServiceAddress);
    }

    [Fact]
    public async Task A_customer_with_two_premises_is_shown_neither_rather_than_an_arbitrary_one()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Taitano Hardware", customerClass: CustomerClass.Commercial);

        await ServedAsync(host, customer, await APremiseAsync(host, "12 Beach St"));
        await ServedAsync(host, customer, await APremiseAsync(host, "45 As Matuis Rd"));

        var hit = Assert.Single((await SearchAsync(host, "Taitano")).Hits);

        Assert.Equal(2, hit.ServiceAccountCount);
        Assert.Null(hit.ServiceAccountNumber);
        Assert.Null(hit.ServiceAddress);
    }

    [Fact]
    public async Task A_customer_with_no_premise_at_all_still_comes_back()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Sablan Family Residence");

        var hit = Assert.Single((await SearchAsync(host, "Sablan")).Hits);

        Assert.Equal(customer.Id, hit.Customer.Id);
        Assert.Equal(0, hit.ServiceAccountCount);
        Assert.Null(hit.ServiceAddress);
    }

    [Fact]
    public async Task A_short_run_of_digits_asks_every_kind_it_could_be()
    {
        // The ambiguous case, answered by dispatching more rather than by choosing. One customer is
        // reachable by the tail of their account number and another by the tail of their telephone;
        // both come back, and precedence decides which the rep sees first.
        using var host = NewHost();

        var byNumber = await ACustomerAsync(host, "Sablan Family Residence");
        var byPhone = await ACustomerAsync(host, "Taitano Hardware", phone: "670-285-0001", customerClass: CustomerClass.Commercial);

        var tail = byNumber.AccountNumber[^4..];

        var result = await SearchAsync(host, tail);

        Assert.Equal(
            [CustomerMatchKind.AccountNumber, CustomerMatchKind.MeterNumber, CustomerMatchKind.Phone],
            result.Kinds);

        Assert.Equal(byNumber.Id, result.Hits[0].Customer.Id);
        Assert.Equal(CustomerMatchKind.AccountNumber, result.Hits[0].MatchedOn);
        Assert.Contains(result.Hits, hit => hit.Customer.Id == byPhone.Id && hit.MatchedOn == CustomerMatchKind.Phone);
    }

    [Fact]
    public async Task A_customer_found_two_ways_appears_once_with_the_better_reason()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Beach Street Holdings", customerClass: CustomerClass.Commercial);
        await ServedAsync(host, customer, await APremiseAsync(host, "12 Beach St"));

        var result = await SearchAsync(host, "Beach Street");

        var hit = Assert.Single(result.Hits);

        Assert.Equal(1, result.Total);
        Assert.Equal(CustomerMatchKind.Name, hit.MatchedOn);
    }

    [Fact]
    public async Task The_status_filter_beside_the_box_narrows_the_search()
    {
        // The search box is the registry's search box, sitting beside the status and class selects.
        // A search that ignored them would answer a question nobody asked.
        using var host = NewHost();

        var active = await ACustomerAsync(host, "Cruz Household");
        var prospect = await ACustomerAsync(host, "Cruz Family Store", customerClass: CustomerClass.Commercial);

        await host.WithTransitionsAsync(transitions =>
            transitions.ChangeStatusAsync(
                active.Id,
                new ChangeCustomerStatusInput(CustomerStatus.Active, TransitionReasonCode.CustomerRequest)));

        var result = await FilteredAsync(host, "Cruz", status: CustomerStatus.Active);

        Assert.Equal(active.Id, Assert.Single(result.Hits).Customer.Id);
        Assert.DoesNotContain(result.Hits, hit => hit.Customer.Id == prospect.Id);
    }

    [Fact]
    public async Task The_class_filter_narrows_a_match_that_arrived_through_a_premise()
    {
        // The filter has to reach all five kinds, not the three that read the customer table
        // directly — a filter that quietly skipped address and meter matches would be worse than
        // none, because it would look like it worked.
        using var host = NewHost();

        var residential = await ACustomerAsync(host, "Sablan Family Residence");
        await ServedAsync(host, residential, await APremiseAsync(host, "12 Beach St"));

        var commercial = await ACustomerAsync(host, "Manglona Store", customerClass: CustomerClass.Commercial);
        await ServedAsync(host, commercial, await APremiseAsync(host, "14 Beach St"));

        var result = await FilteredAsync(host, "Beach St", customerClass: CustomerClass.Commercial);

        var hit = Assert.Single(result.Hits);

        Assert.Equal(commercial.Id, hit.Customer.Id);
        Assert.Equal(CustomerMatchKind.Address, hit.MatchedOn);
    }

    [Fact]
    public async Task A_meter_match_is_narrowed_by_the_filters_too()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Sablan Family Residence");
        var premise = await APremiseAsync(host, "12 Beach St");
        await ServedAsync(host, customer, premise);

        host.Meters.Fitted("MTR-000012", premise.Id);

        // The customer is Residential and still a Prospect; asking for a Commercial one finds the
        // meter and then nobody to route the caller to.
        Assert.Empty((await FilteredAsync(host, "MTR-000012", customerClass: CustomerClass.Commercial)).Hits);
        Assert.Single((await FilteredAsync(host, "MTR-000012", customerClass: CustomerClass.Residential)).Hits);
    }

    [Fact]
    public async Task A_hit_carries_the_whole_customer_so_a_registry_row_and_a_result_row_are_one_row()
    {
        using var host = NewHost();

        var customer = await ACustomerAsync(host, "Taitano Hardware", phone: "670-285-1234", customerClass: CustomerClass.Commercial);

        var hit = Assert.Single((await SearchAsync(host, "Taitano")).Hits);

        // Every column the registry table renders, present on a search row unchanged.
        Assert.Equal(customer.AccountNumber, hit.Customer.AccountNumber);
        Assert.Equal("Taitano Hardware", hit.Customer.Name);
        Assert.Equal(CustomerClass.Commercial, hit.Customer.Class);
        Assert.Equal(CustomerStatus.Prospect, hit.Customer.Status);
        Assert.Equal("670-285-1234", hit.Customer.Phone);
        Assert.Equal(customer.RegisteredAt, hit.Customer.RegisteredAt);
    }
}
