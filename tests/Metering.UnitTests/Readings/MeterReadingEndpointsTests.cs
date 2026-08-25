using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Metering.UnitTests.Readings;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What is asserted
/// is exactly what the routing layer uses to decide 401 vs 403.
/// </summary>
public sealed class MeterReadingEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapMeterReadingEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Theory]
    [InlineData("/api/meter-readings/", "GET")]
    [InlineData("/api/meters/{id:guid}/readings", "GET")]
    public void Reading_the_register_is_gated_on_the_read_permission(string route, string method) =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Metering.Read),
            PolicyOf(EndpointAt(route, method)));

    [Theory]
    [InlineData("/api/meter-readings/cycles", "POST")]
    [InlineData("/api/meters/{id:guid}/readings", "POST")]
    public void Recording_readings_is_gated_on_the_write_permission(string route, string method) =>
        // Failure path in the shape the routing layer enforces it: a caller holding only
        // metering.read — customer service, a manager, a supervisor — is refused with 403 on both of
        // these without the handler running.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Metering.Write),
            PolicyOf(EndpointAt(route, method)));

    [Fact]
    public void Running_a_cycle_needs_no_permission_of_its_own() =>
        // The same call WP-2.1 made for assignment, and deliberately unlike WP-1.4's stock
        // adjustment. Reading meters is the ordinary work of the crews and billing staff WP-0.3
        // already granted metering.write to; a separate permission would be one nobody holds and one
        // more thing to grant before the demo runs.
        Assert.Equal(
            PolicyOf(EndpointAt("/api/meters/{id:guid}/readings", "POST")),
            PolicyOf(EndpointAt("/api/meter-readings/cycles", "POST")));

    [Fact]
    public void No_reading_endpoint_opts_out_of_authentication() =>
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
    public void Nothing_in_the_reading_register_can_be_deleted_or_edited() =>
        // Append-only, and asserted rather than assumed. A figure a bill was raised from must still
        // say what it said years later, so a correction is a new reading rather than an edit.
        Assert.DoesNotContain(
            MappedEndpoints(),
            endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods
                .Any(method => method is "DELETE" or "PUT" or "PATCH"));

    [Fact]
    public void Every_write_endpoint_validates_its_body_before_the_handler_runs() =>
        Assert.All(
            MappedEndpoints().Where(endpoint =>
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("POST")),
            endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<ValidatedRequest>()));

    [Fact]
    public void Every_endpoint_is_named_so_the_route_table_builds()
    {
        // Building the table at all is the assertion: a handler taking a service without
        // [FromServices] fails here rather than at the first request, when the host refuses to start.
        var mapped = MappedEndpoints();

        Assert.NotEmpty(mapped);
        Assert.All(mapped, endpoint => Assert.False(string.IsNullOrWhiteSpace(endpoint.DisplayName)));
    }

    [Fact]
    public void The_meter_register_and_the_reading_register_map_together_without_colliding()
    {
        // Readings hang off the meter as well as standing on their own, so both groups touch
        // /api/meters. A duplicate route would fail at route-build time in the host, not here.
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();

        routes.MapMeterEndpoints();
        routes.MapMeterReadingEndpoints();

        var mapped = routes.DataSources
            .SelectMany(source => source.Endpoints)
            .Cast<RouteEndpoint>()
            .Select(endpoint => $"{string.Join(',', endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)} {endpoint.RoutePattern.RawText}")
            .ToList();

        Assert.Equal(mapped.Count, mapped.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("GET /api/meters/{id:guid}/readings", mapped, StringComparer.Ordinal);
    }
}
