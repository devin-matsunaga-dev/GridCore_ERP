using GridCore.Modules.Customers.Features.Customers;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.Search;

/// <summary>One row of a search result, as the API returns it.</summary>
/// <param name="Customer">
/// The customer, in exactly the shape <c>GET /api/customers</c> returns them. A result row and a
/// registry row are the same row — the search box is the registry's search box — so a caller
/// rendering both switches between two lists of the same thing rather than two different shapes.
/// </param>
/// <param name="MatchedOn">Which field matched — what the row's label says.</param>
/// <param name="IsExact">Whether the whole field matched rather than part of it.</param>
/// <param name="MatchedValue">The stored value that matched, as stored.</param>
/// <param name="ServiceAccountCount">How many accounts they hold that are not closed.</param>
/// <param name="ServiceAccountNumber">The account the row is about, when there is exactly one it could be.</param>
/// <param name="ServiceAddress">Where that account is served, when there is exactly one it could be.</param>
/// <param name="MeterNumber">The meter the match came through, for a meter-number match.</param>
public sealed record CustomerSearchHitResponse(
    CustomerResponse Customer,
    string MatchedOn,
    bool IsExact,
    string MatchedValue,
    int ServiceAccountCount,
    string? ServiceAccountNumber,
    string? ServiceAddress,
    string? MeterNumber)
{
    /// <summary>Projects a hit for the wire, stringifying its enums as every response record here does.</summary>
    public static CustomerSearchHitResponse From(CustomerSearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);

        return new CustomerSearchHitResponse(
            CustomerResponse.From(hit.Customer),
            hit.MatchedOn.ToString(),
            hit.IsExact,
            hit.MatchedValue,
            hit.ServiceAccountCount,
            hit.ServiceAccountNumber,
            hit.ServiceAddress,
            hit.MeterNumber);
    }
}

/// <summary>A page of search results, as the API returns it.</summary>
/// <param name="Term">What was searched for, as typed.</param>
/// <param name="Kinds">The kinds the term was dispatched as, in precedence order.</param>
/// <param name="Hits">This page of rows, best first.</param>
/// <param name="Total">Matching customers across every page.</param>
/// <param name="Page">Which page this is, one-based.</param>
/// <param name="PageSize">How many rows a full page holds.</param>
/// <param name="Truncated">Whether a candidate cap was reached, making <paramref name="Total"/> a floor.</param>
public sealed record CustomerSearchResponse(
    string Term,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<CustomerSearchHitResponse> Hits,
    int Total,
    int Page,
    int PageSize,
    bool Truncated)
{
    /// <summary>Projects a result for the wire.</summary>
    public static CustomerSearchResponse From(CustomerSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CustomerSearchResponse(
            result.Term,
            result.Kinds.Select(kind => kind.ToString()).ToList(),
            result.Hits.Select(CustomerSearchHitResponse.From).ToList(),
            result.Total,
            result.Page,
            result.PageSize,
            result.Truncated);
    }
}

/// <summary>The CSR search box's HTTP surface.</summary>
/// <remarks>
/// <para>
/// A <c>GET</c> sub-resource of the customer registry rather than a resource of its own: it answers
/// with customers, which is not what <c>/api/customer-registrations</c> (WP-2.8) could say for
/// itself. <c>{id:guid}</c> cannot swallow the literal segment, so the two live side by side.
/// </para>
/// <para>
/// Read-only and gated on <see cref="Permissions.Customers.Read"/> — the permission that already
/// opens the registry this searches. Nothing here writes, so there is no audit entry: WP-0.4's
/// invariant is about writes, and an audit trail of every search a rep ran is a surveillance log,
/// not an audit trail. Reprints and statements, which leave the building, are audited by WP-2.14.
/// </para>
/// </remarks>
public static class CustomerSearchEndpoints
{
    /// <summary>Route of the search box.</summary>
    public const string Route = $"{CustomerEndpoints.RoutePrefix}/search";

    /// <summary>Maps the customer search endpoint.</summary>
    public static IEndpointRouteBuilder MapCustomerSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet(Route, async (
                    string? q,
                    CustomerStatus? status,
                    CustomerClass? @class,
                    int? page,
                    int? pageSize,
                    [FromServices] ICustomerSearchService search,
                    CancellationToken cancellationToken) =>
                Results.Ok(CustomerSearchResponse.From(await search.SearchAsync(
                    new CustomerSearchQuery(q, status, @class, page ?? 1, pageSize ?? CustomerSearchQuery.DefaultPageSize),
                    cancellationToken))))
            .RequirePermission(Permissions.Customers.Read)
            .WithTags("Customers")

            // No validator. A blank box, a page past the end and a nonsense page size are all
            // ordinary states of a search box being typed into, and the query record clamps them —
            // answering a rep's half-typed term with a 400 would be the wrong shape of strict.
            .WithName("SearchCustomers");

        return endpoints;
    }
}
