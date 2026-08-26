using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.ServiceAccounts;

/// <summary>Body of a request to open a service account.</summary>
/// <param name="CustomerId">Who is to be served.</param>
/// <param name="ServiceLocationId">Where they are to be served.</param>
/// <param name="Reason">Why, for the account history.</param>
public sealed record OpenServiceAccountRequest(Guid CustomerId, Guid ServiceLocationId, string? Reason = null);

/// <summary>
/// Body of a request to start, stop or close service. One DTO for all three: they carry the same
/// thing, and the verb is the route rather than a field — an operator cannot mistype "stop" as
/// "close" when the URL is what decides.
/// </summary>
/// <param name="Reason">Why, for the account history and the audit trail.</param>
public sealed record ServiceAccountTransitionRequest(string? Reason = null);

/// <summary>One line of an account's service history as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="FromStatus">Where the account was — absent on the opening line.</param>
/// <param name="ToStatus">Where it went.</param>
/// <param name="Reason">Why.</param>
/// <param name="ActorId">Subject id of whoever did it.</param>
/// <param name="ActorName">Their name at the time.</param>
/// <param name="RecordedAt">When.</param>
public sealed record ServiceAccountHistoryEntryResponse(
    Guid Id,
    string? FromStatus,
    string ToStatus,
    string? Reason,
    string ActorId,
    string? ActorName,
    DateTimeOffset RecordedAt)
{
    /// <summary>Projects a history entry for the wire.</summary>
    public static ServiceAccountHistoryEntryResponse From(ServiceAccountHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new ServiceAccountHistoryEntryResponse(
            entry.Id,
            entry.FromStatus?.ToString(),
            entry.ToStatus.ToString(),
            entry.Reason,
            entry.ActorId,
            entry.ActorName,
            entry.RecordedAt);
    }
}

/// <summary>A service account as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="AccountNumber">The number quoted for this supply.</param>
/// <param name="CustomerId">Who is served.</param>
/// <param name="ServiceLocationId">Where.</param>
/// <param name="Status">Where the account stands.</param>
/// <param name="AllowedTransitions">Statuses it may still move to — what a UI renders as buttons.</param>
/// <param name="OpenedAt">When the account was opened.</param>
/// <param name="ServiceStartedAt">When supply was most recently energised.</param>
/// <param name="ServiceEndedAt">When supply was most recently cut.</param>
/// <param name="StatusChangedAt">When the status last moved.</param>
/// <param name="StatusReason">Why it last moved.</param>
/// <param name="History">The account's service history, oldest first. Empty on a list row.</param>
public sealed record ServiceAccountResponse(
    Guid Id,
    string AccountNumber,
    Guid CustomerId,
    Guid ServiceLocationId,
    string Status,
    IReadOnlyList<string> AllowedTransitions,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ServiceStartedAt,
    DateTimeOffset? ServiceEndedAt,
    DateTimeOffset? StatusChangedAt,
    string? StatusReason,
    IReadOnlyList<ServiceAccountHistoryEntryResponse> History)
{
    /// <summary>Projects a <see cref="ServiceAccount"/> for the wire, with whatever history is loaded.</summary>
    public static ServiceAccountResponse From(ServiceAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return new ServiceAccountResponse(
            account.Id,
            account.AccountNumber,
            account.CustomerId,
            account.ServiceLocationId,
            account.Status.ToString(),
            account.AllowedTransitions.Select(status => status.ToString()).ToList(),
            account.OpenedAt,
            account.ServiceStartedAt,
            account.ServiceEndedAt,
            account.StatusChangedAt,
            account.StatusReason,
            account.History
                .OrderBy(entry => entry.Id)
                .Select(ServiceAccountHistoryEntryResponse.From)
                .ToList());
    }
}

/// <summary>The service account registry's HTTP surface.</summary>
public static class ServiceAccountEndpoints
{
    /// <summary>Route prefix of the service account registry.</summary>
    public const string RoutePrefix = "/api/service-accounts";

    /// <summary>Maps the service account endpoints.</summary>
    public static IEndpointRouteBuilder MapServiceAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Service accounts");

        group
            .MapGet("/", async (
                    string? search,
                    Guid? customerId,
                    Guid? serviceLocationId,
                    ServiceAccountStatus? status,
                    int? limit,
                    [FromServices] IServiceAccountService accounts,
                    CancellationToken cancellationToken) =>
                Results.Ok((await accounts.ListAsync(
                        new ServiceAccountQuery(search, customerId, serviceLocationId, status, limit ?? 50),
                        cancellationToken))
                    .Select(ServiceAccountResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("ListServiceAccounts");

        group
            .MapGet("/{id:guid}", async ([FromRoute] Guid id, [FromServices] IServiceAccountService accounts, CancellationToken cancellationToken) =>
            {
                var account = await accounts.FindAsync(id, cancellationToken);

                return account is null ? RegistryProblems.ServiceAccountNotFound(id) : Results.Ok(ServiceAccountResponse.From(account));
            })
            .RequirePermission(Permissions.Customers.Read)
            .WithName("GetServiceAccount");

        // Its own resource rather than a field of the account, because it is a list that grows and
        // an agent reading back "when was I cut off" wants it on its own.
        group
            .MapGet("/{id:guid}/history", ([FromRoute] Guid id, [FromServices] IServiceAccountService accounts, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok((await accounts.HistoryAsync(id, cancellationToken))
                        .Select(ServiceAccountHistoryEntryResponse.From)
                        .ToList())))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("GetServiceAccountHistory");

        group
            .MapPost("/", (OpenServiceAccountRequest body, [FromServices] IServiceAccountService accounts, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var account = await accounts.OpenAsync(
                        new OpenServiceAccountInput(body.CustomerId, body.ServiceLocationId, body.Reason),
                        cancellationToken);

                    return Results.Created($"{RoutePrefix}/{account.Id}", ServiceAccountResponse.From(account));
                }))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<OpenServiceAccountRequest>()
            .WithName("OpenServiceAccount");

        // Start and stop are transitions, not field edits, so each is its own POST sub-resource per
        // CONVENTIONS.md — and an illegal one is a 409 from the aggregate.
        //
        // They keep customers.write and their free-text reason, unlike closing, and that asymmetry is
        // deliberate (WP-2.15): a disconnection is an operational act that leaves the account open
        // and reversible — the supply goes off and can go back on tomorrow — while a closure ends the
        // service period, releases the premise and is what triggers a final bill. The first is
        // clerical; the second is a move-out.
        MapTransition(group, "start", "StartService", (accounts, id, reason, ct) => accounts.StartServiceAsync(id, reason, ct));
        MapTransition(group, "stop", "StopService", (accounts, id, reason, ct) => accounts.StopServiceAsync(id, reason, ct));

        // NO close route since WP-2.15. Closing an account IS a move-out, so it moved to
        // POST /api/customers/{customerId}/transitions/move-out, which demands a reason code from the
        // fixed list, the day service ended and the narrower customers.transition permission. Leaving
        // this one here beside it would have been the bypass that makes "every transition carries a
        // reason code" untrue. IServiceAccountService.CloseAsync is unchanged and is what the
        // transition service calls — the state machine did not move, only the way in.

        return endpoints;
    }

    private static void MapTransition(
        RouteGroupBuilder group,
        string verb,
        string name,
        Func<IServiceAccountService, Guid, string?, CancellationToken, Task<ServiceAccount>> transition) =>
        group
            .MapPost($"/{{id:guid}}/{verb}", (
                    [FromRoute] Guid id,
                    ServiceAccountTransitionRequest body,
                    [FromServices] IServiceAccountService accounts,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(ServiceAccountResponse.From(await transition(accounts, id, body.Reason, cancellationToken)))))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<ServiceAccountTransitionRequest>()
            .WithName(name);
}
