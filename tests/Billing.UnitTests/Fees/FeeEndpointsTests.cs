using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Fees;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Billing.UnitTests.Fees;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What is asserted
/// is exactly what the routing layer uses to decide 401 vs 403, which here is the difference between
/// a schedule anybody in billing can read and a fee somebody can put on a customer's account.
/// </summary>
public sealed class FeeEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapFeeEndpoints();
        routes.MapBillEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Theory]
    [InlineData("/api/fee-schedule/", "GET")]
    [InlineData("/api/account-charges/", "GET")]
    [InlineData("/api/account-charges/{id:guid}", "GET")]
    public void Reading_the_schedule_and_the_charges_is_gated_on_the_read_permission(string route, string method) =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Billing.Read),
            PolicyOf(EndpointAt(route, method)));

    [Theory]
    [InlineData("/api/account-charges/", "POST")]
    [InlineData("/api/account-charges/{id:guid}/cancel", "POST")]
    [InlineData("/api/account-charges/{id:guid}/bill", "POST")]
    public void Charging_a_customer_is_gated_on_the_charge_permission(string route, string method) =>
        // THE FAILURE PATH THIS WORK PACKAGE IS ABOUT, in the shape the routing layer enforces it: a
        // caller holding only billing.read is refused with 403 on every one of these, without the
        // handler running. billing.charge is held by customer service, the Billing role and Managers.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Billing.Charge),
            PolicyOf(EndpointAt(route, method)));

    [Fact]
    public void Charging_is_a_different_gate_from_running_the_billing_cycle()
    {
        // Not a tidier name for billing.generate. Customer service holds billing.charge and does not
        // hold billing.generate, because raising one reconnection fee is not running a cycle over
        // every metered premise on the island — asserting the policies merely differ is what stops a
        // later refactor collapsing them into one.
        Assert.NotEqual(
            PolicyOf(EndpointAt("/api/bills/runs", "POST")),
            PolicyOf(EndpointAt("/api/account-charges/", "POST")));

        Assert.NotEqual(
            PolicyOf(EndpointAt("/api/account-charges/", "GET")),
            PolicyOf(EndpointAt("/api/account-charges/", "POST")));
    }

    [Fact]
    public void Charging_is_a_different_gate_from_adjusting_a_bill() =>
        // Two sensitive money permissions that travel together for Managers and the Billing role and
        // apart for customer service, which holds the charge and not the adjustment. Collapsing them
        // would hand the front desk the power to credit a disputed bill.
        Assert.NotEqual(
            PolicyOf(EndpointAt("/api/bills/{id:guid}/adjustments", "POST")),
            PolicyOf(EndpointAt("/api/account-charges/", "POST")));

    [Fact]
    public void Charging_is_the_only_thing_the_charge_permission_opens()
    {
        // The shape WP-1.4 asserted for inventory.adjust and WP-2.4 for billing.adjust: an ordinary
        // endpoint quietly re-gated on billing.charge would hand the sensitive permission a second
        // door, and one gated back down to billing.read would leave it opening nothing at all.
        var behindCharge = MappedEndpoints()
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(authorize => authorize.Policy == PermissionPolicy.NameFor(Permissions.Billing.Charge)))
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "/api/account-charges/",
                "/api/account-charges/{id:guid}/bill",
                "/api/account-charges/{id:guid}/cancel",
            ],
            behindCharge);
    }

    [Fact]
    public void The_published_schedule_has_no_write_endpoint()
    {
        // A published fee is reference data: changing $135 to $150 is a new effective-dated row in a
        // migration, never an endpoint somebody can point at a production database. The same call
        // WP-0.8 made about the chart of accounts and WP-2.8 about the deposit schedule.
        var methods = MappedEndpoints()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith(FeeEndpoints.SchedulePrefix, StringComparison.Ordinal))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)
            .Distinct()
            .ToList();

        Assert.Equal(["GET"], methods);
    }

    [Fact]
    public void The_charge_register_has_no_delete_or_replace()
    {
        // A raised charge is withdrawn, never deleted, and never edited into a different figure —
        // the append-only habit WP-2.2's readings and WP-2.5's payments both keep.
        var methods = MappedEndpoints()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith(FeeEndpoints.ChargesPrefix, StringComparison.Ordinal))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)
            .Distinct()
            .ToList();

        Assert.DoesNotContain("DELETE", methods);
        Assert.DoesNotContain("PUT", methods);
        Assert.DoesNotContain("PATCH", methods);
    }
}
