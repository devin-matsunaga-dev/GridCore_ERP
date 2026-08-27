using GridCore.Modules.Customers.Features.Arrangements;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.UnitTests.Arrangements;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What is asserted is
/// exactly what the routing layer uses to decide 401 vs 403, which here is the difference between
/// quoting a schedule down the telephone and committing the utility to accept a debt in instalments.
/// </summary>
public sealed class ArrangementEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapArrangementEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Theory]
    [InlineData("/api/service-accounts/{serviceAccountId:guid}/payment-arrangements/", "GET")]
    [InlineData("/api/payment-arrangements/limits", "GET")]
    public void Reading_what_has_been_arranged_and_what_may_be_is_gated_on_the_read_permission(
        string route,
        string method) =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Read),
            PolicyOf(EndpointAt(route, method)));

    [Theory]
    [InlineData("/api/service-accounts/{serviceAccountId:guid}/payment-arrangements/", "POST")]
    [InlineData("/api/service-accounts/{serviceAccountId:guid}/payment-arrangements/{arrangementId:guid}/activation", "POST")]
    [InlineData("/api/payment-arrangements/reviews", "POST")]
    public void Making_moving_and_reviewing_an_arrangement_takes_the_new_grant(string route, string method) =>
        // THE PACKAGE'S GATE. An arrangement suppresses a disconnection while it stands, so it does
        // not travel on customers.write — which is what every clerk who may correct a spelling holds.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Arrange),
            PolicyOf(EndpointAt(route, method)));

    [Fact]
    public void Exactly_three_routes_can_move_an_arrangement() =>
        Assert.Equal(
            [
                "/api/payment-arrangements/reviews",
                "/api/service-accounts/{serviceAccountId:guid}/payment-arrangements/",
                "/api/service-accounts/{serviceAccountId:guid}/payment-arrangements/{arrangementId:guid}/activation",
            ],
            MappedEndpoints()
                .Where(endpoint => PolicyOf(endpoint) == PermissionPolicy.NameFor(Permissions.Customers.Arrange))
                .Select(endpoint => endpoint.RoutePattern.RawText!)
                .Order(StringComparer.Ordinal));

    [Fact]
    public void No_route_carries_a_permission_this_feature_does_not_own() =>
        // An arrangement moves no money and touches no bill, so nothing here may reach
        // customers.deposit or billing.adjust — the two grants a reader might expect to find on a
        // feature about arrears and never should.
        Assert.All(
            MappedEndpoints(),
            endpoint => Assert.Contains(
                PolicyOf(endpoint),
                new[]
                {
                    PermissionPolicy.NameFor(Permissions.Customers.Read),
                    PermissionPolicy.NameFor(Permissions.Customers.Arrange),
                }));

    [Fact]
    public void Bringing_an_arrangement_into_force_is_a_sub_resource_rather_than_a_status_field() =>
        // CONVENTIONS.md: non-CRUD actions are POST sub-resources. Activation has rules behind it —
        // an approval may have to have been granted — and a PATCH of a status column would read as a
        // field being set.
        Assert.DoesNotContain(
            MappedEndpoints(),
            endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("PATCH"));

    [Fact]
    public void The_per_account_surface_hangs_off_the_account_it_is_about() =>
        // An arrangement is always about one supply's arrears at one premise: a customer taking
        // electricity and water may be behind on one and current on the other.
        Assert.All(
            MappedEndpoints().Where(endpoint => endpoint.RoutePattern.RawText!.Contains("service-accounts", StringComparison.Ordinal)),
            endpoint => Assert.StartsWith(
                "/api/service-accounts/{serviceAccountId:guid}/payment-arrangements",
                endpoint.RoutePattern.RawText!,
                StringComparison.Ordinal));

    [Fact]
    public void The_ceilings_and_the_review_run_are_register_wide_rather_than_per_account() =>
        // Neither is about one account, so neither hangs off one — the shape WP-2.19's late-charge
        // run took.
        Assert.Equal(
            ["/api/payment-arrangements/limits", "/api/payment-arrangements/reviews"],
            MappedEndpoints()
                .Select(endpoint => endpoint.RoutePattern.RawText!)
                .Where(route => !route.Contains("service-accounts", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));
}
