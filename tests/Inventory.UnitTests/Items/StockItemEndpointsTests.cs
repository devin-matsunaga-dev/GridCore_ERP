using GridCore.Modules.Inventory.Features.Items;
using GridCore.Modules.Inventory.Features.Warehouses;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Inventory.UnitTests.Items;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What is asserted
/// is exactly what the routing layer uses to decide 401 vs 403, which here is the difference between
/// a storeman who can move stock and one who can make a discrepancy disappear.
/// </summary>
public class StockItemEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapWarehouseEndpoints();
        routes.MapStockItemEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Theory]
    [InlineData("/api/inventory/warehouses", "GET")]
    [InlineData("/api/inventory/items/", "GET")]
    [InlineData("/api/inventory/items/{id:guid}", "GET")]
    [InlineData("/api/inventory/items/{id:guid}/movements", "GET")]
    public void Reading_the_store_is_gated_on_the_read_permission(string route, string method) =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Inventory.Read),
            PolicyOf(EndpointAt(route, method)));

    [Theory]
    [InlineData("/api/inventory/items/", "POST")]
    [InlineData("/api/inventory/items/{id:guid}", "PUT")]
    [InlineData("/api/inventory/items/{id:guid}/receipts", "POST")]
    [InlineData("/api/inventory/items/{id:guid}/issues", "POST")]
    [InlineData("/api/inventory/items/{id:guid}/minimum-quantity", "POST")]
    public void Moving_stock_is_gated_on_the_write_permission(string route, string method) =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Inventory.Write),
            PolicyOf(EndpointAt(route, method)));

    [Fact]
    public void Correcting_a_count_is_gated_on_the_adjust_permission() =>
        // The whole point of the WP's permission story, and the failure path in the shape the routing
        // layer enforces it: a Technician or a Manager holds inventory.read and nothing else, and a
        // storeman holds write — but only Warehouse and Administrator hold inventory.adjust, so
        // everybody else is refused with 403 before the handler runs.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Inventory.Adjust),
            PolicyOf(EndpointAt("/api/inventory/items/{id:guid}/adjustments", "POST")));

    [Fact]
    public void The_adjustment_endpoint_is_the_only_one_demanding_more_than_write()
    {
        // Guards the split rather than the two constants: an adjustment quietly re-gated on
        // inventory.write would leave the sensitive action anyone with a receipt book could perform.
        var adjust = PermissionPolicy.NameFor(Permissions.Inventory.Adjust);

        var gatedOnAdjust = MappedEndpoints()
            .Where(endpoint => PolicyOf(endpoint) == adjust)
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .ToArray();

        Assert.Equal(["/api/inventory/items/{id:guid}/adjustments"], gatedOnAdjust);
    }

    [Fact]
    public void No_store_endpoint_opts_out_of_authentication() =>
        Assert.All(MappedEndpoints(), endpoint =>
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>()));

    [Fact]
    public void Every_permission_the_store_demands_is_one_GridCore_declares() =>
        Assert.All(
            MappedEndpoints()
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy)
                .Where(policy => policy is not null)
                .Select(policy => PermissionPolicy.PermissionFor(policy!)),
            permission => Assert.True(permission is not null && Permissions.All.Contains(permission)));

    [Fact]
    public void Nothing_in_the_store_can_be_deleted() =>
        // A line is discontinued, never deleted: its movements are what a job costing and a
        // valuation read, and the ledger is append-only. Nor is there any endpoint that writes a
        // warehouse — those are reference data, and adding one is a migration.
        Assert.DoesNotContain(
            MappedEndpoints(),
            endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(HttpMethods.Delete));

    [Fact]
    public void The_warehouse_list_is_the_only_warehouse_route_and_it_is_read_only()
    {
        var warehouseRoutes = MappedEndpoints()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/inventory/warehouses", StringComparison.Ordinal))
            .ToList();

        Assert.All(warehouseRoutes, endpoint =>
            Assert.Equal(["GET"], endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods));
    }

    [Fact]
    public void Every_write_endpoint_validates_its_body_before_the_handler_runs()
    {
        // A write that skipped its validator would reach the aggregate, throw, and answer 409 or 500
        // for what is plainly a 400 — so "has a filter" is asserted, not assumed.
        var writes = MappedEndpoints().Where(endpoint =>
            endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods
                .Any(method => method is "POST" or "PUT"));

        Assert.All(writes, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<ValidatedRequest>()));
    }
}
