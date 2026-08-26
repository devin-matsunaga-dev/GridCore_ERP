using GridCore.Modules.Billing.Features.Documents;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Billing.UnitTests.Documents;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier.
/// </summary>
/// <remarks>
/// What it pins is the surprising half of WP-2.14: the reprint lives in Billing and is gated on a
/// <b>Customers</b> permission, because from the desk it is the same act as the statement beside it.
/// A change of mind about that shows up here first.
/// </remarks>
public class BillDocumentEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapBillDocumentEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint TheDocumentEndpoint() => Assert.Single(MappedEndpoints());

    [Fact]
    public void A_bills_document_hangs_off_the_bill() =>
        Assert.Equal("/api/bills/{billId:guid}/document", BillDocumentEndpoints.DocumentRoute);

    [Fact]
    public void It_is_a_GET_though_it_writes_an_audit_entry() =>
        // Read-side: nothing about the bill moves, and asking twice gives the same document. A POST
        // would say the utility keeps a register of reprints, which it deliberately does not.
        Assert.Equal(
            ["GET"],
            TheDocumentEndpoint().Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

    [Fact]
    public void Producing_a_copy_is_gated_on_customers_documents_and_NOT_on_billing_read()
    {
        var policy = TheDocumentEndpoint().Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

        Assert.Equal(PermissionPolicy.NameFor(Permissions.Customers.Documents), policy);
        Assert.NotEqual(PermissionPolicy.NameFor(Permissions.Billing.Read), policy);
    }

    [Fact]
    public void Every_route_demands_a_permission() =>
        Assert.All(MappedEndpoints(), endpoint => Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));
}
