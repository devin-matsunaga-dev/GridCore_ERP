using GridCore.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Platform.UnitTests.Security;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What is asserted
/// is exactly what the routing layer uses to decide 401 vs 403.
/// </summary>
public class MeEndpointsTests
{
    private static IReadOnlyList<Endpoint> MappedEndpoints()
    {
        // WebApplication implements IEndpointRouteBuilder explicitly; nothing is started.
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapMeEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints)];
    }

    private static Endpoint EndpointAt(string route) =>
        MappedEndpoints().Single(endpoint =>
            ((RouteEndpoint)endpoint).RoutePattern.RawText == route);

    [Fact]
    public void Both_identity_endpoints_are_mapped()
    {
        var routes = MappedEndpoints().Select(e => ((RouteEndpoint)e).RoutePattern.RawText).ToList();

        Assert.Contains(MeEndpoints.MeRoute, routes);
        Assert.Contains(MeEndpoints.PermissionProbeRoute, routes);
    }

    [Fact]
    public void The_current_user_endpoint_carries_no_permission_of_its_own()
    {
        // Any signed-in role may ask who they are; the host's fallback policy still requires a token.
        Assert.Empty(EndpointAt(MeEndpoints.MeRoute).Metadata.GetOrderedMetadata<IAuthorizeData>());
    }

    [Fact]
    public void The_probe_endpoint_is_gated_on_the_admin_permission()
    {
        var authorize = Assert.Single(EndpointAt(MeEndpoints.PermissionProbeRoute).Metadata.GetOrderedMetadata<IAuthorizeData>());

        Assert.Equal(PermissionPolicy.NameFor(Permissions.Platform.Admin), authorize.Policy);
    }

    [Fact]
    public void Neither_endpoint_opts_out_of_authentication()
    {
        Assert.All(MappedEndpoints(), endpoint =>
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>()));
    }
}
