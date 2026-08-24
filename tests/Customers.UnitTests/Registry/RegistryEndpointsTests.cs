using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
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
    public void Reading_the_registry_is_gated_on_the_read_permission(string route, string method) =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Read),
            PolicyOf(EndpointAt(route, method)));

    [Theory]
    [InlineData("/api/customers/", "POST")]
    [InlineData("/api/customers/{id:guid}", "PUT")]
    [InlineData("/api/customers/{id:guid}/status", "POST")]
    [InlineData("/api/service-locations/", "POST")]
    [InlineData("/api/service-locations/{id:guid}", "PUT")]
    [InlineData("/api/service-accounts/", "POST")]
    [InlineData("/api/service-accounts/{id:guid}/start", "POST")]
    [InlineData("/api/service-accounts/{id:guid}/stop", "POST")]
    [InlineData("/api/service-accounts/{id:guid}/close", "POST")]
    public void Writing_to_the_registry_is_gated_on_the_write_permission(string route, string method) =>
        // Failure path in the shape the routing layer enforces it: a caller holding only
        // customers.read is refused with 403 on every one of these, without the handler running.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Write),
            PolicyOf(EndpointAt(route, method)));

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
