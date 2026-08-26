using GridCore.Modules.Customers.Features.Delinquency;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.UnitTests.Delinquency;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What is asserted is
/// exactly what the routing layer uses to decide 401 vs 403, which here is the difference between
/// quoting arrears down the telephone and spending somebody's deposit.
/// </summary>
public sealed class DelinquencyEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapDelinquencyEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Theory]
    [InlineData("/api/service-accounts/{serviceAccountId:guid}/delinquency", "GET")]
    [InlineData("/api/service-accounts/{serviceAccountId:guid}/dunning-notices", "GET")]
    public void Reading_the_picture_and_the_notices_is_gated_on_the_read_permission(string route, string method) =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Read),
            PolicyOf(EndpointAt(route, method)));

    [Fact]
    public void Serving_a_notice_is_clerical_and_gated_on_write() =>
        // The same grant an intake takes. Recording that a letter went out is clerical work; deciding
        // to spend the customer's deposit is not, which is the next test.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Write),
            PolicyOf(EndpointAt("/api/service-accounts/{serviceAccountId:guid}/dunning-notices", "POST")));

    [Fact]
    public void Evaluating_for_disconnection_is_gated_on_the_DEPOSIT_permission() =>
        // THE PACKAGE'S SHARPEST GATE. Evaluating eligibility sets a customer's deposit against what
        // they owe, so it is a deposit movement wearing a decision's clothes — gating it on
        // customers.write would be a way of spending a deposit without holding the permission to
        // spend one. The service demands it again, before it reads anything.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Deposit),
            PolicyOf(EndpointAt("/api/service-accounts/{serviceAccountId:guid}/disconnection-eligibility", "POST")));

    [Fact]
    public void Evaluating_is_a_POST_because_it_moves_money() =>
        // A GET that applied a deposit would apply it again on every refresh. The read that shows the
        // same figures without moving them is /delinquency.
        Assert.DoesNotContain(
            MappedEndpoints(),
            endpoint =>
                endpoint.RoutePattern.RawText!.EndsWith("disconnection-eligibility", StringComparison.Ordinal)
                && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("GET"));

    [Fact]
    public void Exactly_one_route_can_spend_a_deposit() =>
        Assert.Equal(
            ["/api/service-accounts/{serviceAccountId:guid}/disconnection-eligibility"],
            MappedEndpoints()
                .Where(endpoint => PolicyOf(endpoint) == PermissionPolicy.NameFor(Permissions.Customers.Deposit))
                .Select(endpoint => endpoint.RoutePattern.RawText));

    [Fact]
    public void The_delinquency_surface_hangs_off_the_service_account_it_is_about() =>
        // Unlike WP-2.18's applications, which are worked from a queue across every customer.
        // Delinquency is always about one supply at one premise, so the account is the resource
        // rather than a filter.
        Assert.All(
            MappedEndpoints(),
            endpoint => Assert.StartsWith(
                "/api/service-accounts/{serviceAccountId:guid}",
                endpoint.RoutePattern.RawText!,
                StringComparison.Ordinal));
}
