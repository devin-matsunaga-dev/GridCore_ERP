using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Billing.UnitTests.Bills;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What is asserted
/// is exactly what the routing layer uses to decide 401 vs 403, which here is the difference between
/// a register a manager can read and one they can raise money with.
/// </summary>
public sealed class BillEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapBillEndpoints();
        routes.MapRatePlanEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Theory]
    [InlineData("/api/bills/", "GET")]
    [InlineData("/api/bills/{id:guid}", "GET")]
    [InlineData("/api/rate-plans/", "GET")]
    [InlineData("/api/rate-plans/{code}", "GET")]
    [InlineData("/api/account-rate-plans/{serviceAccountId:guid}", "GET")]
    public void Reading_bills_and_tariffs_is_gated_on_the_read_permission(string route, string method) =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Billing.Read),
            PolicyOf(EndpointAt(route, method)));

    [Theory]
    [InlineData("/api/bills/runs", "POST")]
    [InlineData("/api/bills/{id:guid}/issue", "POST")]
    [InlineData("/api/bills/{id:guid}/cancel", "POST")]
    [InlineData("/api/bills/overdue-review", "POST")]
    [InlineData("/api/account-rate-plans/{serviceAccountId:guid}", "PUT")]
    public void Raising_money_is_gated_on_the_generate_permission(string route, string method) =>
        // Failure path in the shape the routing layer enforces it: a caller holding only
        // billing.read — customer service, a manager, Finance — is refused with 403 on every one of
        // these, without the handler running. WP-0.3 granted billing.generate to the Billing role
        // and to Administrator, and to nobody else.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Billing.Generate),
            PolicyOf(EndpointAt(route, method)));

    [Fact]
    public void Setting_an_accounts_tariff_needs_more_than_read()
    {
        // What a customer is charged on is not something a caller with read-only access changes.
        // It is deliberately the same gate as running a billing run rather than a permission of its
        // own: setting the tariff an account bills on IS part of running billing, and a separate
        // permission would be one nobody holds and one more thing to grant before the demo runs —
        // the call WP-2.1 and WP-2.2 both made.
        Assert.NotEqual(
            PolicyOf(EndpointAt("/api/account-rate-plans/{serviceAccountId:guid}", "GET")),
            PolicyOf(EndpointAt("/api/account-rate-plans/{serviceAccountId:guid}", "PUT")));

        Assert.Equal(
            PolicyOf(EndpointAt("/api/bills/runs", "POST")),
            PolicyOf(EndpointAt("/api/account-rate-plans/{serviceAccountId:guid}", "PUT")));
    }

    [Fact]
    public void Adjusting_a_bill_is_not_reachable_yet() =>
        // billing.adjust exists (WP-0.3) and WP-2.4 owns the endpoint behind it. Nothing here claims
        // it, so a permission granted to Managers and the Billing role opens nothing until then.
        Assert.DoesNotContain(
            MappedEndpoints()
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy),
            policy => policy == PermissionPolicy.NameFor(Permissions.Billing.Adjust));

    [Fact]
    public void No_billing_endpoint_opts_out_of_authentication() =>
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
    public void Nothing_in_the_billing_register_can_be_deleted() =>
        // No DELETE here either. A bill is a document the utility has to be able to reproduce and
        // defend years later; withdrawing one is a Cancelled status with a reason, which keeps
        // saying what it said.
        Assert.DoesNotContain(
            MappedEndpoints(),
            endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(HttpMethods.Delete));

    [Fact]
    public void A_tariff_cannot_be_published_or_repriced_through_the_API()
    {
        // Tariffs are reference data (invariant 8): a migrated database can bill without anybody
        // configuring anything, and changing a published rate is a migration, not a screen. The
        // only write under /api/rate-plans* is the ACCOUNT assignment, which is a different noun.
        var writes = MappedEndpoints()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/rate-plans", StringComparison.Ordinal))
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods
                .Any(method => method is "POST" or "PUT" or "PATCH" or "DELETE"));

        Assert.Empty(writes);
    }

    [Fact]
    public void Nothing_issues_a_bill_by_editing_a_field()
    {
        // Issuing is a POST sub-resource per CONVENTIONS.md. A PUT that could set the status would
        // be a way round the state machine and round the BillIssued event Finance posts on.
        var puts = MappedEndpoints()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("PUT"))
            .Select(endpoint => endpoint.Metadata.GetMetadata<ValidatedRequest>()!.RequestType);

        Assert.All(puts, requestType =>
            Assert.DoesNotContain(
                requestType.GetProperties(),
                property => property.Name.Contains("Status", StringComparison.Ordinal)));
    }

    [Fact]
    public void Every_write_endpoint_validates_its_body_before_the_handler_runs()
    {
        // A write that skipped its validator would reach the aggregate, throw, and answer 409 or 500
        // for what is plainly a 400 — so "has a filter" is asserted, not assumed.
        var writes = MappedEndpoints().Where(endpoint =>
            endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods
                .Any(method => method is "POST" or "PUT"));

        Assert.NotEmpty(writes);
        Assert.All(writes, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<ValidatedRequest>()));
    }

    [Fact]
    public void Every_endpoint_is_named_so_a_client_can_be_generated() =>
        Assert.All(
            MappedEndpoints(),
            endpoint => Assert.False(string.IsNullOrWhiteSpace(endpoint.DisplayName)));
}
