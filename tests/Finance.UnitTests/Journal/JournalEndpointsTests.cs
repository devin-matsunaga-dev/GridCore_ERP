using GridCore.Modules.Finance.Features.Journal;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Finance.UnitTests.Journal;

/// <summary>
/// Endpoint metadata only — no server is started, so this stays in the fast tier. What is asserted
/// is exactly what the routing layer uses to decide 401 vs 403, plus the two things this work
/// package deliberately did <i>not</i> build.
/// </summary>
public sealed class JournalEndpointsTests
{
    private static IReadOnlyList<RouteEndpoint> MappedEndpoints()
    {
        IEndpointRouteBuilder routes = WebApplication.CreateBuilder().Build();
        routes.MapJournalEndpoints();

        return [.. routes.DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>()];
    }

    private static RouteEndpoint EndpointAt(string route, string method) =>
        MappedEndpoints().Single(endpoint =>
            endpoint.RoutePattern.RawText == route
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static string? PolicyOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;

    [Theory]
    [InlineData("/api/finance/accounts", "GET")]
    [InlineData("/api/finance/journal-entries", "GET")]
    [InlineData("/api/finance/journal-entries/{id:guid}", "GET")]
    [InlineData("/api/finance/trial-balance", "GET")]
    [InlineData("/api/finance/accounts-receivable", "GET")]
    public void Reading_the_ledger_is_gated_on_the_finance_read_permission(string route, string method) =>
        // WP-0.3 granted finance.read to the Finance role, to Billing, to Managers and to
        // Administrator. A caller holding none of those — a technician, a warehouse clerk — is
        // refused with 403 on every one of these, without the handler running.
        Assert.Equal(
            PermissionPolicy.NameFor(Permissions.Finance.Read),
            PolicyOf(EndpointAt(route, method)));

    [Fact]
    public void The_ledger_has_no_write_surface_at_all()
    {
        // THE FAILURE PATH THIS WORK PACKAGE IS ABOUT, and it is a structural one. The ledger is
        // append-only and its only author is the event seam: nothing can be posted, corrected or
        // withdrawn over HTTP, so there is no route that could put a figure in a trial balance that
        // no upstream fact explains.
        var methods = MappedEndpoints()
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["GET"], methods);
    }

    [Fact]
    public void Posting_a_manual_journal_entry_is_still_claimed_by_nothing()
    {
        // finance.post is declared and granted (to Finance and to Administrator) and opens no route.
        // SPEC.md does not ask for a manual journal, and a ledger whose only author is the event
        // seam cannot disagree with the modules upstream of it. The day a route demands this
        // permission, that is a deliberate act — this test is what makes it one.
        Assert.DoesNotContain(
            MappedEndpoints(),
            endpoint => PolicyOf(endpoint) == PermissionPolicy.NameFor(Permissions.Finance.Post));
    }

    [Fact]
    public void Refunding_a_payment_is_still_claimed_by_nothing_either()
    {
        // WP-2.5 left payments.refund unclaimed because a refund needs a ledger to post the reversal
        // into. WP-2.6 builds that ledger — and building it is not the same act as performing a
        // refund, which needs a route, a permission gate and a reversal posting of its own.
        Assert.DoesNotContain(
            MappedEndpoints(),
            endpoint => PolicyOf(endpoint) == PermissionPolicy.NameFor(Permissions.Payments.Refund));
    }

    [Fact]
    public void Every_finance_route_sits_under_the_modules_own_prefix() =>
        Assert.All(
            MappedEndpoints(),
            endpoint => Assert.StartsWith(
                JournalEndpoints.RoutePrefix,
                endpoint.RoutePattern.RawText!,
                StringComparison.Ordinal));
}
