using GridCore.Modules.Customers.Features.Applications;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.UnitTests.Applications;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What it pins is
/// the routing layer's half of WP-2.18's authorization: which routes carry
/// <c>customers.approve</c>, which deliberately do not, and that the document's bytes are gated in
/// the service rather than here.
/// </summary>
public class ApplicationEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapApplicationEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    private static IEnumerable<string> RoutesDemanding(string permission) =>
        MappedEndpoints()
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(authorize => authorize.Policy == PermissionPolicy.NameFor(permission)))
            .Select(endpoint => endpoint.RoutePattern.RawText!);

    [Fact]
    public void Deciding_an_application_is_the_only_thing_gated_on_the_approve_permission() =>
        // Two routes and exactly two: approve and reject. A withdrawal is the applicant's own act and
        // filing one is clerical, so neither may drift onto this grant without this test noticing.
        Assert.Equal(
            [$"{ApplicationEndpoints.RoutePrefix}/{{id:guid}}/approve", $"{ApplicationEndpoints.RoutePrefix}/{{id:guid}}/reject"],
            RoutesDemanding(Permissions.Customers.Approve).Order(StringComparer.Ordinal));

    [Fact]
    public void Withdrawing_is_gated_on_write_rather_than_on_approve() =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Write),
            PolicyOf(EndpointAt($"{ApplicationEndpoints.RoutePrefix}/{{id:guid}}/withdraw", "POST")));

    [Theory]
    [InlineData("/", "POST")]
    [InlineData("/{id:guid}/review", "POST")]
    [InlineData("/{id:guid}/documents", "POST")]
    [InlineData("/{id:guid}/resubmissions", "POST")]
    public void The_clerical_routes_are_gated_on_the_write_permission(string suffix, string method) =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Write),
            PolicyOf(EndpointAt(ApplicationEndpoints.RoutePrefix + suffix, method)));

    [Theory]
    [InlineData("/", "GET")]
    [InlineData("/{id:guid}", "GET")]
    public void The_reads_are_gated_on_the_read_permission(string suffix, string method) =>
        // A clerk who may decide nothing still has to be able to tell an applicant what is
        // outstanding, which is the call WP-2.12 made about the deposit ledger.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Read),
            PolicyOf(EndpointAt(ApplicationEndpoints.RoutePrefix + suffix, method)));

    [Fact]
    public void No_route_demands_the_documents_permission()
    {
        // The gate on a scanned identity page is real and it is in the service. Putting
        // customers.documents on the route would gate the whole application read behind it, which
        // is a different and wider rule than "the bytes are a document leaving the building".
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Read),
            PolicyOf(EndpointAt($"{ApplicationEndpoints.RoutePrefix}/{{id:guid}}/documents/{{documentId:guid}}/content", "GET")));

        Assert.Empty(RoutesDemanding(Permissions.Customers.Documents));
    }

    [Fact]
    public void The_reference_data_a_form_reads_is_gated_on_the_read_permission() =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Read),
            PolicyOf(EndpointAt(ApplicationEndpoints.ReferenceRoute, "GET")));

    [Fact]
    public void Every_route_in_the_group_demands_a_permission_GridCore_declares() =>
        Assert.All(
            MappedEndpoints()
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy),
            policy =>
            {
                Assert.NotNull(policy);

                var permission = PermissionPolicy.PermissionFor(policy);

                Assert.True(permission is not null && Permissions.All.Contains(permission));
            });

    [Fact]
    public void The_reference_projection_repeats_the_domains_own_checklist_rather_than_a_copy_of_it()
    {
        var reference = ApplicationReferenceResponse.Current();

        Assert.Equal(
            [.. Enum.GetValues<ServiceApplicationType>().Select(type => type.ToString())],
            reference.Types.Select(type => type.Type));

        Assert.All(
            reference.Types,
            type => Assert.Equal(
                [.. ServiceApplicationTypes
                    .RequiredDocuments(Enum.Parse<ServiceApplicationType>(type.Type))
                    .Select(kind => kind.ToString())],
                type.RequiredDocuments));

        Assert.Equal(ApplicationDocuments.MaxSizeInBytes, reference.MaxSizeInBytes);
        Assert.Equal(3, reference.ReasonCodes.Count);
        Assert.Contains(nameof(ApplicationReasonCode.Other), reference.ReasonCodesRequiringNotes);
    }
}
