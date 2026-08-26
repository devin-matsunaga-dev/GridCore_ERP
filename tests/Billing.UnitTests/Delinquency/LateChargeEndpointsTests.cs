using GridCore.Modules.Billing.Features.Delinquency;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Billing.UnitTests.Delinquency;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What is asserted is
/// exactly what the routing layer uses to decide 401 vs 403 on the late-charge run.
/// </summary>
public sealed class LateChargeEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapLateChargeEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Fact]
    public void Reading_what_has_been_charged_is_gated_on_the_read_permission() =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Billing.Read),
            PolicyOf(EndpointAt("/api/late-charges/", "GET")));

    [Fact]
    public void Running_the_late_charges_is_gated_on_the_charge_permission() =>
        // Not a grant of its own: every act the run performs is raising a published fee against an
        // account, which is exactly what billing.charge names. A second grant covering the same act
        // would be two grants for one job, and the first utility to cut a role would get them out of
        // step. The service demands it again, for the monthly job that will not pass a URL.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Billing.Charge),
            PolicyOf(EndpointAt("/api/late-charges/runs", "POST")));

    [Fact]
    public void The_run_is_a_POST_to_a_collection_of_runs_rather_than_a_POST_to_a_verb() =>
        // The shape the billing cycle and the overdue review already take: running the late charges
        // produces a record of having run them, and the response is that record.
        Assert.Contains(MappedEndpoints(), endpoint => endpoint.RoutePattern.RawText == "/api/late-charges/runs");

    [Fact]
    public void Exactly_one_route_can_raise_money_and_it_is_the_run() =>
        Assert.Equal(
            ["/api/late-charges/runs"],
            MappedEndpoints()
                .Where(endpoint => PolicyOf(endpoint) == PermissionPolicy.NameFor(Permissions.Billing.Charge))
                .Select(endpoint => endpoint.RoutePattern.RawText));
}
