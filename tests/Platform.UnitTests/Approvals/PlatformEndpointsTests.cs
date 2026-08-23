using GridCore.Platform.Approvals;
using GridCore.Platform.Audit;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Platform.UnitTests.Approvals;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What is asserted
/// is exactly what the routing layer uses to decide 401 vs 403.
/// </summary>
public class PlatformEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapAuditEndpoints();
        routes.MapApprovalEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Theory]
    [InlineData("/api/approvals/{id:guid}/approve")]
    [InlineData("/api/approvals/{id:guid}/reject")]
    public void Deciding_is_gated_on_the_approve_permission(string route) =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Platform.Approve),
            PolicyOf(EndpointAt(route, HttpMethods.Post)));

    [Fact]
    public void Reading_the_audit_trail_is_gated_on_the_audit_read_permission() =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Platform.AuditRead),
            PolicyOf(EndpointAt(AuditEndpoints.Route, HttpMethods.Get)));

    [Fact]
    public void The_pending_queue_is_gated_on_the_approve_permission() =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Platform.Approve),
            PolicyOf(EndpointAt(ApprovalEndpoints.RoutePrefix + "/", HttpMethods.Get)));

    [Theory]
    [InlineData("/api/approvals/", "POST")]
    [InlineData("/api/approvals/{id:guid}/cancel", "POST")]
    public void Raising_and_withdrawing_need_only_a_signed_in_caller(string route, string method) =>
        // Authorization with no policy: the fallback policy still demands a token, but no
        // permission — anyone may ask, and only the requester may withdraw (enforced in the service).
        Assert.Null(PolicyOf(EndpointAt(route, method)));

    [Fact]
    public void No_platform_endpoint_opts_out_of_authentication() =>
        Assert.All(MappedEndpoints(), endpoint =>
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>()));

    [Fact]
    public void Every_permission_the_platform_endpoints_demand_is_one_GridCore_declares() =>
        Assert.All(
            MappedEndpoints()
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy)
                .Where(policy => policy is not null)
                .Select(policy => PermissionPolicy.PermissionFor(policy!)),
            permission => Assert.True(permission is not null && Permissions.All.Contains(permission)));
}
