using GridCore.Modules.Customers.Features.Contacts;
using GridCore.Modules.Customers.Features.Profile;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.UnitTests.Contacts;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What it pins is
/// the routing layer's half of this work package's authorization, and the boundary between that
/// half and the service's: every route demands <c>customers.write</c> or <c>customers.read</c>, and
/// the disclosure gate cannot live here because whether a request moves the flag is a fact about
/// the body compared against what is stored.
/// </summary>
public class ContactEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();

        routes.MapContactEndpoints();
        routes.MapProfileEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    /// <summary>A group's own root, which <c>MapGroup(prefix).MapGet("/")</c> spells with a trailing slash.</summary>
    private static string Root(string prefix) => $"{prefix}/";

    [Fact]
    public void Reading_a_customer_s_contacts_needs_the_read_permission() =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Read),
            PolicyOf(EndpointAt(Root(ContactEndpoints.CustomerContactsRoute), "GET")));

    [Fact]
    public void Adding_a_contact_needs_the_write_permission() =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Write),
            PolicyOf(EndpointAt(Root(ContactEndpoints.CustomerContactsRoute), "POST")));

    [Fact]
    public void Reading_the_profile_needs_the_read_permission() =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Read),
            PolicyOf(EndpointAt(Root(ProfileEndpoints.RoutePrefix), "GET")));

    [Fact]
    public void Saving_the_profile_needs_the_write_permission() =>
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Customers.Write),
            PolicyOf(EndpointAt(Root(ProfileEndpoints.RoutePrefix), "PUT")));

    [Fact]
    public void No_route_demands_the_authorise_permission() =>
        // The gate is real and it is in the service. A route-level customers.authorise would refuse
        // a rep correcting a spelling on a contact somebody else authorised — which any clerk may do
        // — and would let a body that quietly flips the flag through on any other route. It is the
        // wrong shape, not a stricter one. This is the assertion to keep.
        Assert.DoesNotContain(
            MappedEndpoints()
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy),
            policy => policy == PermissionPolicy.NameFor(Permissions.Customers.Authorise));

    [Fact]
    public void Every_permission_these_routes_demand_is_one_GridCore_declares() =>
        Assert.All(
            MappedEndpoints()
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(authorize => authorize.Policy)
                .Where(policy => policy is not null)
                .Select(policy => PermissionPolicy.PermissionFor(policy!)),
            permission => Assert.True(permission is not null && Permissions.All.Contains(permission)));

    [Fact]
    public void No_endpoint_opts_out_of_authentication() =>
        Assert.All(MappedEndpoints(), endpoint =>
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>()));

    [Fact]
    public void Every_write_validates_its_body_before_the_handler_runs() =>
        // A write that skipped its validator would reach the aggregate, throw, and answer 409 or 500
        // for what is plainly a 400. Promotion and removal carry no body, so they have nothing to
        // validate — they are excluded by having no body, not by exception.
        Assert.All(
            new[]
            {
                EndpointAt(Root(ContactEndpoints.CustomerContactsRoute), "POST"),
                EndpointAt($"{ContactEndpoints.RoutePrefix}/{{contactId:guid}}", "PUT"),
                EndpointAt($"{ContactEndpoints.RoutePrefix}/{{contactId:guid}}/methods", "POST"),
                EndpointAt($"{ContactEndpoints.RoutePrefix}/{{contactId:guid}}/methods/{{methodId:guid}}", "PUT"),
                EndpointAt(Root(ProfileEndpoints.RoutePrefix), "PUT"),
            },
            endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<ValidatedRequest>()));

    [Fact]
    public void Promotion_is_a_post_sub_resource_rather_than_a_field_edit() =>
        // CONVENTIONS.md: non-CRUD actions are POST sub-resources. It is also the one write here
        // that changes a row the caller did not name — the method holding the primary place is
        // demoted in the same act — which is exactly why it is not a PUT of a boolean.
        Assert.Equal(
            [HttpMethods.Post],
            EndpointAt($"{ContactEndpoints.RoutePrefix}/{{contactId:guid}}/methods/{{methodId:guid}}/primary", "POST")
                .Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

    [Fact]
    public void The_profile_is_saved_whole_rather_than_patched() =>
        // A partial save is how a cleared mailing address and an omitted one become impossible to
        // tell apart, and that distinction is the entire point of this resource.
        Assert.DoesNotContain(
            MappedEndpoints()
                .Where(endpoint => endpoint.RoutePattern.RawText == Root(ProfileEndpoints.RoutePrefix))
                .SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods),
            method => method == HttpMethods.Patch);
}
