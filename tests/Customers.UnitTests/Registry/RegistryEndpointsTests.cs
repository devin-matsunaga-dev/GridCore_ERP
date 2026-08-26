using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Transitions;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What is asserted
/// is exactly what the routing layer uses to decide 401 vs 403, which is the difference between a
/// registry a technician can read and one they can rewrite.
/// </summary>
public class RegistryEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapCustomerEndpoints();
        routes.MapServiceLocationEndpoints();
        routes.MapServiceAccountEndpoints();
        routes.MapTransitionEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Theory]
    [InlineData("/api/customers/", "GET")]
    [InlineData("/api/customers/{id:guid}", "GET")]
    [InlineData("/api/service-locations/", "GET")]
    [InlineData("/api/service-locations/{id:guid}", "GET")]
    [InlineData("/api/service-accounts/", "GET")]
    [InlineData("/api/service-accounts/{id:guid}", "GET")]
    [InlineData("/api/service-accounts/{id:guid}/history", "GET")]
    [InlineData("/api/customers/{customerId:guid}/transitions/", "GET")]
    public void Reading_the_registry_is_gated_on_the_read_permission(string route, string method) =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Read),
            PolicyOf(EndpointAt(route, method)));

    [Theory]
    [InlineData("/api/customers/", "POST")]
    [InlineData("/api/customers/{id:guid}", "PUT")]
    [InlineData("/api/service-locations/", "POST")]
    [InlineData("/api/service-locations/{id:guid}", "PUT")]
    [InlineData("/api/service-accounts/", "POST")]

    // Start and stop keep customers.write; closing does not appear here at all any more. WP-2.15
    // moved it to the move-out transition, because closing an account ends the service period and
    // triggers a final bill, while a disconnection leaves the account open and reversible.
    [InlineData("/api/service-accounts/{id:guid}/start", "POST")]
    [InlineData("/api/service-accounts/{id:guid}/stop", "POST")]
    public void Writing_to_the_registry_is_gated_on_the_write_permission(string route, string method) =>
        // Failure path in the shape the routing layer enforces it: a caller holding only
        // customers.read is refused with 403 on every one of these, without the handler running.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Write),
            PolicyOf(EndpointAt(route, method)));

    [Theory]
    [InlineData("/api/customers/{customerId:guid}/transitions/class", "POST")]
    [InlineData("/api/customers/{customerId:guid}/transitions/status", "POST")]
    [InlineData("/api/customers/{customerId:guid}/transitions/move-in", "POST")]
    [InlineData("/api/customers/{customerId:guid}/transitions/move-out", "POST")]
    [InlineData("/api/customers/{customerId:guid}/transitions/transfer", "POST")]
    public void Moving_a_customer_is_gated_on_the_narrower_transition_permission(string route, string method) =>
        // WP-2.15. Narrower than customers.write on purpose: these are the changes that alter what a
        // customer is billed, and a clerk who may correct a spelling is not automatically a clerk who
        // may re-classify somebody. The service demands it again — see the transition service's
        // remarks — because it is reachable in process.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Transition),
            PolicyOf(EndpointAt(route, method)));

    [Fact]
    public void No_route_changes_a_customer_class_or_status_without_a_reason_code() =>
        // The rule of WP-2.15, asserted as the absence it depends on. A second way in — the old
        // POST /api/customers/{id}/status, or a class field on the correction PUT — would make "every
        // transition carries a reason code from the fixed list" true of one route and false of
        // another, which is the shape WP-2.12 refused when it took the deposit out of the update body.
        Assert.All(
            MappedEndpoints().Where(endpoint =>
                endpoint.RoutePattern.RawText!.Contains("/transitions/", StringComparison.Ordinal) is false),
            endpoint => Assert.True(
                endpoint.Metadata.GetMetadata<ValidatedRequest>() is not { RequestType: var body }
                || !body.IsAssignableTo(typeof(ITransitionRequest)),
                $"{endpoint.RoutePattern.RawText} accepts a transition body outside the transitions group."));

    [Fact]
    public void No_registry_endpoint_opts_out_of_authentication() =>
        Assert.All(MappedEndpoints(), endpoint =>
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>()));

    [Fact]
    public void Every_permission_the_registry_demands_is_one_GridCore_declares() =>
        Assert.All(
            MappedEndpoints()
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy)
                .Where(policy => policy is not null)
                .Select(policy => PermissionPolicy.PermissionFor(policy!)),
            permission => Assert.True(permission is not null && Permissions.All.Contains(permission)));

    [Fact]
    public void Nothing_in_the_registry_can_be_deleted() =>
        // Deactivation and closure are the only ways out, deliberately: meters, work orders and
        // bills reference a location and an account, and a deleted row would take their context
        // with it — along with the service history somebody may have to answer for.
        Assert.DoesNotContain(
            MappedEndpoints(),
            endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(HttpMethods.Delete));

    [Fact]
    public void Every_write_endpoint_validates_its_body_before_the_handler_runs()
    {
        // A write that skipped its validator would reach the aggregate, throw, and answer 409 or
        // 500 for what is plainly a 400 — so "has a filter" is asserted, not assumed.
        var writes = MappedEndpoints().Where(endpoint =>
            endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods
                .Any(method => method is "POST" or "PUT"));

        Assert.All(writes, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<ValidatedRequest>()));
    }
}
