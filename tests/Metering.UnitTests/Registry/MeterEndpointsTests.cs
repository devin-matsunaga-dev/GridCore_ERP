using GridCore.Modules.Metering.Features.Meters;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Metering.UnitTests.Registry;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What is asserted
/// is exactly what the routing layer uses to decide 401 vs 403, which is the difference between a
/// register a billing officer can read and one they can rewrite.
/// </summary>
public sealed class MeterEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapMeterEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Theory]
    [InlineData("/api/meters/", "GET")]
    [InlineData("/api/meters/{id:guid}", "GET")]
    [InlineData("/api/meters/{id:guid}/history", "GET")]
    public void Reading_the_register_is_gated_on_the_read_permission(string route, string method) =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Metering.Read),
            PolicyOf(EndpointAt(route, method)));

    [Theory]
    [InlineData("/api/meters/", "POST")]
    [InlineData("/api/meters/{id:guid}", "PUT")]
    [InlineData("/api/meters/{id:guid}/assign", "POST")]
    [InlineData("/api/meters/{id:guid}/remove", "POST")]
    [InlineData("/api/meters/{id:guid}/status", "POST")]
    public void Writing_to_the_register_is_gated_on_the_write_permission(string route, string method) =>
        // Failure path in the shape the routing layer enforces it: a caller holding only
        // metering.read — customer service, a manager, a supervisor — is refused with 403 on every
        // one of these, without the handler running.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Metering.Write),
            PolicyOf(EndpointAt(route, method)));

    [Fact]
    public void Assignment_needs_no_permission_of_its_own()
    {
        // Deliberately unlike WP-1.4's stock adjustment, which is gated above write. Fitting a
        // meter is the ordinary work of the crews and billing staff who already hold metering.write
        // (WP-0.3 granted it to Billing, Technician and Administrator); a separate permission would
        // be one nobody holds and one more thing to grant before the demo runs.
        Assert.Equal(
            PolicyOf(EndpointAt("/api/meters/", "POST")),
            PolicyOf(EndpointAt("/api/meters/{id:guid}/assign", "POST")));
    }

    [Fact]
    public void No_register_endpoint_opts_out_of_authentication() =>
        Assert.All(MappedEndpoints(), endpoint =>
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>()));

    [Fact]
    public void Every_permission_the_register_demands_is_one_GridCore_declares() =>
        Assert.All(
            MappedEndpoints()
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy)
                .Where(policy => policy is not null)
                .Select(policy => PermissionPolicy.PermissionFor(policy!)),
            permission => Assert.True(permission is not null && Permissions.All.Contains(permission)));

    [Fact]
    public void Nothing_in_the_register_can_be_deleted() =>
        // Retirement is the only way out, as in every other GridCore registry: readings, bills and
        // disputes all point at the meter that produced them, and a deleted row would take their
        // context with it.
        Assert.DoesNotContain(
            MappedEndpoints(),
            endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(HttpMethods.Delete));

    [Fact]
    public void Nothing_moves_a_meter_onto_a_premise_by_editing_a_field()
    {
        // Assignment is a POST sub-resource per CONVENTIONS.md. A PUT that could set
        // service_location_id would be a way round the one-meter-per-premise check and the history
        // line that records who fitted it and what the dials read.
        var puts = MappedEndpoints()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("PUT"))
            .Select(endpoint => endpoint.Metadata.GetMetadata<ValidatedRequest>()!.RequestType);

        Assert.All(puts, requestType =>
            Assert.DoesNotContain(
                requestType.GetProperties(),
                property => property.Name.Contains("ServiceLocation", StringComparison.Ordinal)));
    }

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

    [Fact]
    public void Every_endpoint_is_named_so_the_route_table_builds()
    {
        // Building the table at all is the assertion: a handler taking a service without
        // [FromServices] fails here — "Body was inferred but the method does not allow inferred body
        // parameters" — rather than at the first request, when the host refuses to start (WP-1.4).
        var mapped = MappedEndpoints();

        Assert.NotEmpty(mapped);
        Assert.All(mapped, endpoint => Assert.False(string.IsNullOrWhiteSpace(endpoint.DisplayName)));
    }
}
