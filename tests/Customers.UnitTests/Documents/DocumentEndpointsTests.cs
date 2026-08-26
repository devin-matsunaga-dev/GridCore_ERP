using GridCore.Modules.Customers.Features.Documents;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.UnitTests.Documents;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier.
/// </summary>
/// <remarks>
/// What it pins is the line WP-2.14 drew and WP-2.13 drew on the other side: producing a document
/// earns a permission of its own, where logging a call did not. A document leaves the building.
/// </remarks>
public class DocumentEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapDocumentEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route) =>
        MappedEndpoints().Single(endpoint => endpoint.RoutePattern.RawText == route);

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Fact]
    public void The_documents_hang_off_the_customer_they_are_about()
    {
        Assert.Equal("/api/customers/{customerId:guid}/documents", DocumentEndpoints.RoutePrefix);
        Assert.Equal("/statement", DocumentEndpoints.StatementRoute);
        Assert.Equal("/payment-history", DocumentEndpoints.PaymentHistoryRoute);
    }

    [Theory]
    [InlineData("/api/customers/{customerId:guid}/documents/statement")]
    [InlineData("/api/customers/{customerId:guid}/documents/payment-history")]
    public void Producing_a_document_is_gated_on_customers_documents(string route) =>
        // NOT customers.read, which is what opened the page. Reading a balance on screen and handing
        // somebody a statement of it are different acts — the opposite call from WP-2.13's notes,
        // which ride on the ordinary permissions because logging a call is clerical work.
        Assert.Equal(PermissionPolicy.NameFor(Permissions.Customers.Documents), PolicyOf(EndpointAt(route)));

    [Theory]
    [InlineData("/api/customers/{customerId:guid}/documents/statement")]
    [InlineData("/api/customers/{customerId:guid}/documents/payment-history")]
    public void Both_are_GETs_though_both_write_an_audit_entry(string route) =>
        // Read-side: nothing moves, and asking twice gives the same document. A POST would say the
        // utility keeps a register of statements, which it deliberately does not — a statement is
        // composed from records that already exist.
        Assert.Equal(["GET"], EndpointAt(route).Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

    [Fact]
    public void Every_route_demands_a_permission() =>
        Assert.All(MappedEndpoints(), endpoint => Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));

    [Fact]
    public void Nothing_here_claims_a_permission_this_module_does_not_own() =>
        Assert.All(
            MappedEndpoints(),
            endpoint => Assert.Equal(PermissionPolicy.NameFor(Permissions.Customers.Documents), PolicyOf(endpoint)));

    [Fact]
    public void A_statement_asked_for_without_a_range_covers_the_last_quarter() =>
        // A rep opening the tab wants a statement, not a date form. Ninety days is what "send me a
        // statement" means before the two selects narrow it.
        Assert.Equal(90, DocumentEndpoints.DefaultRangeDays);
}
