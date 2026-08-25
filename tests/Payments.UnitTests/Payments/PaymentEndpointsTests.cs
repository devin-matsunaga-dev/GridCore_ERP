using GridCore.Modules.Payments.Features.Payments;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Payments.UnitTests.Payments;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What is asserted
/// is exactly what the routing layer uses to decide 401 vs 403, which here is the difference between
/// a register a manager can read and one they can take money with.
/// </summary>
public sealed class PaymentEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapPaymentEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Theory]
    [InlineData("/api/payments/", "GET")]
    [InlineData("/api/payments/{id:guid}", "GET")]
    public void Reading_payments_is_gated_on_the_read_permission(string route, string method) =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Payments.Read),
            PolicyOf(EndpointAt(route, method)));

    [Fact]
    public void Taking_money_is_gated_on_the_record_permission() =>
        // The failure path in the shape the routing layer enforces it: a caller holding only
        // payments.read — the Billing role, Finance, a manager — is refused with 403 here, without
        // the handler running. WP-0.3 granted payments.record to customer service and to
        // Administrator, and to nobody else.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Payments.Record),
            PolicyOf(EndpointAt("/api/payments/", "POST")));

    [Fact]
    public void Taking_money_is_a_different_gate_from_reading_the_register() =>
        // Asserting the policies merely differ is what stops a later refactor collapsing them into
        // one, which would hand every reader the ability to charge a customer.
        Assert.NotEqual(
            PolicyOf(EndpointAt("/api/payments/", "GET")),
            PolicyOf(EndpointAt("/api/payments/", "POST")));

    [Fact]
    public void Nothing_yet_demands_the_refund_permission()
    {
        // WP-2.5 treats Refunded as an outcome the provider seam can carry and nothing more: a
        // refund needs a ledger to post the reversal into, and Finance's does not exist until
        // WP-2.6. payments.refund is declared and granted to Finance and to Administrator, and it
        // opens nothing at all — the same shape WP-2.3 asserted for billing.adjust before WP-2.4
        // claimed it. The day a route does demand it, that is a deliberate act and this test says
        // so.
        var behindRefund = MappedEndpoints()
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(authorize => authorize.Policy == PermissionPolicy.NameFor(Permissions.Payments.Refund)))
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        Assert.Empty(behindRefund);
    }

    [Fact]
    public void A_payment_is_never_edited_or_withdrawn()
    {
        // The register is append-only in the same spirit as the reading register: a retry is a new
        // payment, and money coming back is a refund with its own row. A PUT, a PATCH or a DELETE
        // would be the one route round both the state machine and the audit trail.
        var methods = MappedEndpoints()
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)
            .Distinct()
            .ToList();

        Assert.DoesNotContain("PUT", methods);
        Assert.DoesNotContain("PATCH", methods);
        Assert.DoesNotContain("DELETE", methods);
    }

    [Fact]
    public void No_payments_endpoint_opts_out_of_authentication() =>
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
