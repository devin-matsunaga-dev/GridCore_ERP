using System.Net;
using GridCore.Modules.Customers.Features.Notes;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.UnitTests.Notes;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier.
/// </summary>
/// <remarks>
/// What it pins is the note surface's authorization and the shape of its append-only refusal. Notes
/// ride on the ordinary customer permissions rather than earning one of their own: logging a call is
/// clerical work, which is what separates this package from WP-2.12's deposits and WP-2.11's
/// authorisation flag.
/// </remarks>
public class NoteEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapNoteEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Fact]
    public void A_customers_log_hangs_off_the_customer_and_one_note_is_addressed_on_its_own()
    {
        // Two prefixes, the shape ContactEndpoints established: the alternative makes a client quote
        // an id it already holds twice over.
        Assert.Equal("/api/customers/{customerId:guid}/notes", NoteEndpoints.CustomerNotesRoute);
        Assert.Equal("/api/customer-notes", NoteEndpoints.RoutePrefix);
    }

    [Theory]
    [InlineData("/api/customers/{customerId:guid}/notes/", "GET")]
    [InlineData("/api/customer-notes/{noteId:guid}", "GET")]
    public void Reading_the_log_is_gated_on_the_read_permission(string route, string method) =>
        // What a rep answering the telephone already holds.
        Assert.Equal(PermissionPolicy.NameFor(Permissions.Customers.Read), PolicyOf(EndpointAt(route, method)));

    [Theory]
    [InlineData("/api/customers/{customerId:guid}/notes/", "POST")]
    [InlineData("/api/customer-notes/{noteId:guid}/corrections", "POST")]
    [InlineData("/api/customer-notes/{noteId:guid}/pin", "PUT")]
    public void Writing_to_the_log_is_gated_on_the_write_permission(string route, string method) =>
        // Deliberately NOT a permission of its own. Logging a call is clerical work — inventing
        // `customers.note` would mean a rep who may open an account cannot record that they spoke to
        // somebody about it. That is the line WP-2.11 and WP-2.12 drew on the other side, for acts
        // that disclose a customer's affairs or move money.
        Assert.Equal(PermissionPolicy.NameFor(Permissions.Customers.Write), PolicyOf(EndpointAt(route, method)));

    [Fact]
    public void Every_route_demands_a_permission() =>
        // Invariant 5 is about sensitive acts; this is the weaker rule that nothing here is
        // anonymous, asserted so an added route cannot quietly skip the gate.
        Assert.All(MappedEndpoints(), endpoint => Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));

    [Fact]
    public void Nothing_here_claims_a_permission_this_module_does_not_own() =>
        Assert.All(
            MappedEndpoints(),
            endpoint => Assert.Contains(
                PolicyOf(endpoint),
                new[]
                {
                    PermissionPolicy.NameFor(Permissions.Customers.Read),
                    PermissionPolicy.NameFor(Permissions.Customers.Write),
                }));

    [Fact]
    public void The_only_way_to_DELETE_a_note_is_that_there_is_not_one() =>
        // Append-only means append-only. A delete route would be the second way the register loses
        // history, after an edit — and unlike an edit, nobody would reach for it by accident, so
        // there is nothing to explain and nothing to map.
        Assert.DoesNotContain(
            MappedEndpoints(),
            endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("DELETE"));

    [Fact]
    public void The_PUT_on_a_note_EXISTS_so_that_the_refusal_can_explain_itself()
    {
        // WORK_PACKAGES.md: "edit attempt → 409". An absent route answers 405 Method Not Allowed,
        // which tells a client the verb is unsupported here — where the truth is that this register
        // is append-only and /corrections is what they want. So the route is mapped and always
        // refuses.
        var edit = EndpointAt("/api/customer-notes/{noteId:guid}", "PUT");

        Assert.Equal(PermissionPolicy.NameFor(Permissions.Customers.Write), PolicyOf(edit));
    }

    [Fact]
    public void The_append_only_refusal_is_a_409_naming_the_corrections_sub_resource()
    {
        var id = Guid.CreateVersion7();

        var problem = Assert.IsType<ProblemHttpResult>(RegistryProblems.NoteLogIsAppendOnly(id));

        Assert.Equal((int)HttpStatusCode.Conflict, problem.StatusCode);

        var detail = Assert.IsType<ProblemDetails>(problem.ProblemDetails).Detail!;

        // The route is quoted so a client reading the response knows where to go next — this is the
        // rule of the package somebody is most likely to discover by trying it.
        Assert.Contains("append-only", detail, StringComparison.Ordinal);
        Assert.Contains($"/api/customer-notes/{id}/corrections", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/customers/{customerId:guid}/notes/", "POST", typeof(LogNoteRequest))]
    [InlineData("/api/customer-notes/{noteId:guid}/corrections", "POST", typeof(CorrectNoteRequest))]
    [InlineData("/api/customer-notes/{noteId:guid}/pin", "PUT", typeof(PinNoteRequest))]
    public void Every_body_the_endpoints_accept_is_validated_at_the_edge(string route, string method, Type body) =>
        // The filter is what turns a malformed body into a 400 rather than an exception surfacing
        // from the aggregate. It throws when no validator is registered, so this and
        // CustomersModuleTests are two halves of one guarantee.
        Assert.Equal(body, EndpointAt(route, method).Metadata.GetMetadata<ValidatedRequest>()!.RequestType);
}
