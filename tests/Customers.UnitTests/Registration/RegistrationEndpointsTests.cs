using GridCore.Modules.Customers.Features.Registration;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.UnitTests.Registration;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What it pins is
/// the routing layer's half of the intake's authorization, and the boundary between that half and
/// the service's: the route demands <c>customers.write</c>, and the deposit's own gate cannot live
/// here because whether an intake collects one is a fact about the body.
/// </summary>
public class RegistrationEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapRegistrationEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Fact]
    public void An_intake_is_gated_on_the_write_permission() =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Write),
            PolicyOf(EndpointAt(RegistrationEndpoints.RoutePrefix, "POST")));

    [Fact]
    public void The_deposit_schedule_is_gated_on_the_read_permission() =>
        // Not on customers.deposit: a clerk who may not take a deposit still has to be able to tell
        // a caller on the telephone what one would cost.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Read),
            PolicyOf(EndpointAt(RegistrationEndpoints.DepositRulesRoute, "GET")));

    [Fact]
    public void No_route_demands_the_deposit_permission() =>
        // The gate is real and it is in the service, because it depends on what is in the body. A
        // route-level customers.deposit would refuse an intake that collects nothing — which any
        // clerk may perform — and is therefore the wrong shape, not a stricter one.
        Assert.DoesNotContain(
            MappedEndpoints()
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy),
            policy => policy == PermissionPolicy.NameFor(Permissions.Customers.Deposit));

    [Fact]
    public void Every_permission_the_intake_demands_is_one_GridCore_declares() =>
        Assert.All(
            MappedEndpoints()
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy)
                .Where(policy => policy is not null)
                .Select(policy => PermissionPolicy.PermissionFor(policy!)),
            permission => Assert.True(permission is not null && Permissions.All.Contains(permission)));

    [Fact]
    public void No_intake_endpoint_opts_out_of_authentication() =>
        Assert.All(MappedEndpoints(), endpoint =>
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>()));

    [Fact]
    public void An_intake_validates_its_body_before_the_handler_runs() =>
        // A write that skipped its validator would reach the aggregate, throw, and answer 409 or
        // 500 for what is plainly a 400.
        Assert.NotNull(
            EndpointAt(RegistrationEndpoints.RoutePrefix, "POST").Metadata.GetMetadata<ValidatedRequest>());

    [Fact]
    public void The_deposit_schedule_is_read_only() =>
        // Reference data is corrected by migration, exactly as the chart of accounts is. Nothing
        // here may write one, so there is no runtime path to a deposit figure nobody signed off.
        Assert.All(
            MappedEndpoints().Where(endpoint =>
                endpoint.RoutePattern.RawText == RegistrationEndpoints.DepositRulesRoute),
            endpoint => Assert.Equal(
                [HttpMethods.Get],
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods));

    [Fact]
    public void Nothing_about_an_intake_can_be_deleted() =>
        Assert.DoesNotContain(
            MappedEndpoints(),
            endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(HttpMethods.Delete));
}
