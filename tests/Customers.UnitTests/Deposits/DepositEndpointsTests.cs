using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.UnitTests.Deposits;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier.
/// </summary>
/// <remarks>
/// What it pins is the deposit surface's half of the authorization, and the boundary this work
/// package drew differently from WP-2.8's. An intake takes a deposit as one optional field of a
/// composite request, so its route could not tell whether money was involved and the gate had to
/// live in the service. Every write here <i>is</i> a deposit movement, so the route says so — and
/// the read deliberately does not, because a clerk who may not take money still has to be able to
/// tell a caller what the utility is holding.
/// </remarks>
public class DepositEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapDepositEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Fact]
    public void The_ledger_hangs_off_the_customer_whose_deposit_it_is() =>
        Assert.Equal("/api/customers/{customerId:guid}/deposits", DepositEndpoints.RoutePrefix);

    [Fact]
    public void Reading_the_ledger_is_gated_on_the_read_permission() =>
        // Not on customers.deposit. A clerk who may not move money still has to be able to tell a
        // caller on the telephone what is being held — the call WP-2.8 made about the schedule.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Read),
            PolicyOf(EndpointAt(DepositEndpoints.RoutePrefix + "/", "GET")));

    [Theory]
    [InlineData("/collections")]
    [InlineData("/applications")]
    [InlineData("/refunds")]
    public void Every_movement_is_gated_on_the_deposit_permission(string route) =>
        // Invariant 5. customers.write is not enough for any of these: opening an account and
        // taking money for it are two different jobs, which is the whole reason WP-2.8 cut a
        // narrower permission rather than reusing the broad one.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Deposit),
            PolicyOf(EndpointAt(DepositEndpoints.RoutePrefix + route, "POST")));

    [Fact]
    public void No_route_here_settles_for_the_write_permission() =>
        // Asserted rather than assumed: customers.write opens every other door in this module, and
        // a movement that slipped back to it would let a clerk refund a deposit to themselves.
        Assert.DoesNotContain(
            MappedEndpoints()
                .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("POST"))
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy),
            policy => policy == PermissionPolicy.NameFor(Permissions.Customers.Write));

    [Fact]
    public void Nothing_maps_a_verb_that_could_edit_the_balance_directly() =>
        // The balance is a projection of immutable entries, so there is no PUT and no PATCH — WP-2.12
        // exists to remove exactly that. Movements are POST sub-resources per CONVENTIONS.md.
        Assert.All(
            MappedEndpoints().SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods),
            method => Assert.Contains(method, new[] { "GET", "POST" }));

    [Fact]
    public void Every_permission_the_ledger_demands_is_one_GridCore_declares() =>
        Assert.All(
            MappedEndpoints()
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy)
                .Where(policy => policy is not null)
                .Select(policy => PermissionPolicy.PermissionFor(policy!)),
            permission => Assert.True(permission is not null && Permissions.All.Contains(permission)));

    [Theory]
    [InlineData("/collections", typeof(CollectDepositRequest))]
    [InlineData("/applications", typeof(ApplyDepositRequest))]
    [InlineData("/refunds", typeof(RefundDepositRequest))]
    public void Every_movement_validates_its_body_at_the_edge(string route, Type body) =>
        // A malformed amount is answered as a 400 before the transaction opens, rather than as a
        // workflow conflict from inside it.
        Assert.Equal(
            body,
            EndpointAt(DepositEndpoints.RoutePrefix + route, "POST").Metadata.GetMetadata<ValidatedRequest>()?.RequestType);
}
