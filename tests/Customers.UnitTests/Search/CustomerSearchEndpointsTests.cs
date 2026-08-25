using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Search;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.UnitTests.Search;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What it pins is
/// that the search box reads and cannot write, and that it is gated on the permission that already
/// opens the registry it searches.
/// </summary>
public class CustomerSearchEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapCustomerSearchEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static IReadOnlyList<string> MethodsOf(RouteEndpoint endpoint) =>
        [.. endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods];

    [Fact]
    public void The_search_box_is_gated_on_the_read_permission() =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Read),
            Assert.Single(Assert.Single(MappedEndpoints()).Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);

    [Fact]
    public void Searching_maps_no_verb_but_GET() =>
        // A search reads. The day one of these answers a POST is the day somebody has hung a write on
        // the one route in this module that produces no audit entry.
        Assert.Equal(["GET"], MethodsOf(Assert.Single(MappedEndpoints())));

    [Fact]
    public void The_route_sits_under_the_registry_it_searches() =>
        // A GET sub-resource of /api/customers rather than a resource of its own: it answers with
        // customers, which is not something /api/customer-registrations could say for itself. The
        // guid constraint on {id} cannot swallow the literal segment, so the two live side by side.
        Assert.Equal($"{CustomerEndpoints.RoutePrefix}/search", Assert.Single(MappedEndpoints()).RoutePattern.RawText);

    [Fact]
    public void No_route_demands_the_deposit_permission() =>
        // Nothing here touches money. Asserted rather than assumed, because customers.deposit is the
        // narrowest permission in the module and a route that demanded it would silently stop a
        // Manager from looking anybody up.
        Assert.DoesNotContain(
            MappedEndpoints()
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy),
            policy => policy == PermissionPolicy.NameFor(Permissions.Customers.Deposit));

    [Fact]
    public void Every_permission_the_search_demands_is_one_GridCore_declares() =>
        Assert.All(
            MappedEndpoints()
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy)
                .Where(policy => policy is not null)
                .Select(policy => PermissionPolicy.PermissionFor(policy!)),
            permission => Assert.True(permission is not null && Permissions.All.Contains(permission)));

    [Fact]
    public void A_hit_carries_the_whole_customer_and_crosses_the_wire_with_its_enums_as_names()
    {
        // The row a search returns is the row the registry returns, plus why it matched — which is
        // what lets one table render both. No response record in GridCore exposes a raw enum either
        // (the rule GridCoreJson's reading half depends on, WP-2.8), and the match kind is the new
        // one here.
        var customer = Customer.Register(
            "C-000012",
            "Sablan Family Residence",
            CustomerClass.Residential,
            DateTimeOffset.UnixEpoch,
            phone: "670-285-1234");

        var response = CustomerSearchHitResponse.From(new CustomerSearchHit(
            customer,
            CustomerMatchKind.AccountNumber,
            IsExact: true,
            "C-000012",
            ServiceAccountCount: 1,
            "A-000012",
            "12 Beach St, Songsong, Rota",
            MeterNumber: null));

        Assert.Equal("Residential", response.Customer.Class);
        Assert.Equal("Prospect", response.Customer.Status);
        Assert.Equal("AccountNumber", response.MatchedOn);
    }
}
