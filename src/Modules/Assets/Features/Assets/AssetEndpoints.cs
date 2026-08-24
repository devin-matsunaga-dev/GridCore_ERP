using GridCore.Modules.Assets.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Assets.Features.Assets;

/// <summary>Body of a request to enter an asset in the register.</summary>
/// <param name="Class">What kind of plant it is.</param>
/// <param name="Name">What it is called.</param>
/// <param name="SerialNumber">The manufacturer's serial number, where the plant carries one.</param>
/// <param name="Manufacturer">Who made it.</param>
/// <param name="Model">Their model designation.</param>
/// <param name="InstalledOn">When it was installed.</param>
/// <param name="Latitude">Degrees north. Supply with <paramref name="Longitude"/> or not at all.</param>
/// <param name="Longitude">Degrees east. Supply with <paramref name="Latitude"/> or not at all.</param>
/// <param name="LocationNote">Where it is, in a crew's words.</param>
/// <param name="Status">Where it starts. Most plant is received into storage.</param>
/// <param name="Condition">How it was graded on arrival, if anybody looked.</param>
/// <param name="Note">Why it is being registered, for the history.</param>
public sealed record RegisterAssetRequest(
    AssetClass Class,
    string Name,
    string? SerialNumber = null,
    string? Manufacturer = null,
    string? Model = null,
    DateOnly? InstalledOn = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    string? LocationNote = null,
    AssetStatus Status = AssetTransitions.Initial,
    AssetCondition Condition = AssetCondition.Unknown,
    string? Note = null) : IAssetDetails;

/// <summary>Body of a request to correct an asset's details.</summary>
/// <param name="Class">What kind of plant it is.</param>
/// <param name="Name">What it is called.</param>
/// <param name="SerialNumber">The manufacturer's serial number.</param>
/// <param name="Manufacturer">Who made it.</param>
/// <param name="Model">Their model designation.</param>
/// <param name="InstalledOn">When it was installed.</param>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
/// <param name="LocationNote">Where it is, in a crew's words.</param>
public sealed record UpdateAssetRequest(
    AssetClass Class,
    string Name,
    string? SerialNumber = null,
    string? Manufacturer = null,
    string? Model = null,
    DateOnly? InstalledOn = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    string? LocationNote = null) : IAssetDetails;

/// <summary>Body of a request to move an asset through its lifecycle.</summary>
/// <param name="Status">Where it should end up.</param>
/// <param name="Reason">Why, for the history and the audit trail.</param>
public sealed record ChangeAssetStatusRequest(AssetStatus Status, string? Reason = null);

/// <summary>Body of a request to record an inspector's grading.</summary>
/// <param name="Condition">How the asset was graded.</param>
/// <param name="Note">What the inspector found.</param>
public sealed record AssessAssetConditionRequest(AssetCondition Condition, string? Note = null);

/// <summary>One line of an asset's history as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="EntryType">What kind of thing happened.</param>
/// <param name="FromStatus">Where the asset was, on a lifecycle line.</param>
/// <param name="ToStatus">Where it went, on a lifecycle line.</param>
/// <param name="FromCondition">How it was graded before, on an assessment line.</param>
/// <param name="ToCondition">How it is graded now, on an assessment line.</param>
/// <param name="Note">Why, or what was done.</param>
/// <param name="WorkOrderId">The job it was done under, on a maintenance line.</param>
/// <param name="ActorId">Subject id of whoever did it.</param>
/// <param name="ActorName">Their name at the time.</param>
/// <param name="RecordedAt">When.</param>
public sealed record AssetHistoryEntryResponse(
    Guid Id,
    string EntryType,
    string? FromStatus,
    string? ToStatus,
    string? FromCondition,
    string? ToCondition,
    string? Note,
    Guid? WorkOrderId,
    string ActorId,
    string? ActorName,
    DateTimeOffset RecordedAt)
{
    /// <summary>Projects a history entry for the wire.</summary>
    public static AssetHistoryEntryResponse From(AssetHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new AssetHistoryEntryResponse(
            entry.Id,
            entry.EntryType.ToString(),
            entry.FromStatus?.ToString(),
            entry.ToStatus?.ToString(),
            entry.FromCondition?.ToString(),
            entry.ToCondition?.ToString(),
            entry.Note,
            entry.WorkOrderId,
            entry.ActorId,
            entry.ActorName,
            entry.RecordedAt);
    }
}

/// <summary>An asset as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="AssetTag">The tag stencilled on the plant.</param>
/// <param name="Class">What kind of plant it is.</param>
/// <param name="Name">What it is called.</param>
/// <param name="SerialNumber">The manufacturer's serial number.</param>
/// <param name="Manufacturer">Who made it.</param>
/// <param name="Model">Their model designation.</param>
/// <param name="InstalledOn">When it was installed.</param>
/// <param name="Status">Where it stands in its working life.</param>
/// <param name="AllowedTransitions">Statuses it may still move to — what a UI renders as buttons.</param>
/// <param name="Condition">How it is graded.</param>
/// <param name="Latitude">Degrees north, where a position is recorded.</param>
/// <param name="Longitude">Degrees east, where a position is recorded.</param>
/// <param name="LocationNote">Where it is, in a crew's words.</param>
/// <param name="RegisteredAt">When it was entered in the register.</param>
/// <param name="StatusChangedAt">When the status last moved.</param>
/// <param name="StatusReason">Why it last moved.</param>
/// <param name="ConditionAssessedAt">When the condition was last assessed.</param>
/// <param name="History">The asset's history, oldest first. Empty on a list row.</param>
public sealed record AssetResponse(
    Guid Id,
    string AssetTag,
    string Class,
    string Name,
    string? SerialNumber,
    string? Manufacturer,
    string? Model,
    DateOnly? InstalledOn,
    string Status,
    IReadOnlyList<string> AllowedTransitions,
    string Condition,
    decimal? Latitude,
    decimal? Longitude,
    string? LocationNote,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? StatusChangedAt,
    string? StatusReason,
    DateTimeOffset? ConditionAssessedAt,
    IReadOnlyList<AssetHistoryEntryResponse> History)
{
    /// <summary>Projects an <see cref="Asset"/> for the wire, with whatever history is loaded.</summary>
    public static AssetResponse From(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        return new AssetResponse(
            asset.Id,
            asset.AssetTag,
            asset.Class.ToString(),
            asset.Name,
            asset.SerialNumber,
            asset.Manufacturer,
            asset.Model,
            asset.InstalledOn,
            asset.Status.ToString(),
            asset.AllowedTransitions.Select(status => status.ToString()).ToList(),
            asset.Condition.ToString(),
            asset.Position?.Latitude,
            asset.Position?.Longitude,
            asset.LocationNote,
            asset.RegisteredAt,
            asset.StatusChangedAt,
            asset.StatusReason,
            asset.ConditionAssessedAt,
            asset.History
                .OrderBy(entry => entry.Id)
                .Select(AssetHistoryEntryResponse.From)
                .ToList());
    }
}

/// <summary>The asset register's HTTP surface.</summary>
public static class AssetEndpoints
{
    /// <summary>Route prefix of the asset register.</summary>
    public const string RoutePrefix = "/api/assets";

    /// <summary>Maps the asset endpoints.</summary>
    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Assets");

        group
            .MapGet("/", async (
                    string? search,
                    AssetClass? @class,
                    AssetStatus? status,
                    AssetCondition? condition,
                    int? limit,
                    [FromServices] IAssetService assets,
                    CancellationToken cancellationToken) =>
                Results.Ok((await assets.ListAsync(
                        new AssetQuery(search, @class, status, condition, limit ?? 50),
                        cancellationToken))
                    .Select(AssetResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Assets.Read)
            .WithName("ListAssets");

        group
            .MapGet("/{id:guid}", async ([FromRoute] Guid id, [FromServices] IAssetService assets, CancellationToken cancellationToken) =>
            {
                var asset = await assets.FindAsync(id, cancellationToken);

                return asset is null ? AssetProblems.AssetNotFound(id) : Results.Ok(AssetResponse.From(asset));
            })
            .RequirePermission(Permissions.Assets.Read)
            .WithName("GetAsset");

        // The maintenance-history read model. Its own resource rather than a field of the asset,
        // because it is a list that grows with every inspection and every job — and `?entryType=`
        // is what narrows it to the maintenance lines WP-3.4 writes.
        group
            .MapGet("/{id:guid}/history", (
                    [FromRoute] Guid id,
                    AssetHistoryEntryType? entryType,
                    [FromServices] IAssetService assets,
                    CancellationToken cancellationToken) =>
                AssetProblems.RunAsync(async () =>
                    Results.Ok((await assets.HistoryAsync(id, entryType, cancellationToken))
                        .Select(AssetHistoryEntryResponse.From)
                        .ToList())))
            .RequirePermission(Permissions.Assets.Read)
            .WithName("GetAssetHistory");

        group
            .MapPost("/", (RegisterAssetRequest body, [FromServices] IAssetService assets, CancellationToken cancellationToken) =>
                AssetProblems.RunAsync(async () =>
                {
                    var asset = await assets.RegisterAsync(
                        new RegisterAssetInput(
                            body.Class,
                            body.Name,
                            body.SerialNumber,
                            body.Manufacturer,
                            body.Model,
                            body.InstalledOn,
                            body.Latitude,
                            body.Longitude,
                            body.LocationNote,
                            body.Status,
                            body.Condition,
                            body.Note),
                        cancellationToken);

                    return Results.Created($"{RoutePrefix}/{asset.Id}", AssetResponse.From(asset));
                }))
            .RequirePermission(Permissions.Assets.Write)
            .WithValidation<RegisterAssetRequest>()
            .WithName("RegisterAsset");

        group
            .MapPut("/{id:guid}", ([FromRoute] Guid id, UpdateAssetRequest body, [FromServices] IAssetService assets, CancellationToken cancellationToken) =>
                AssetProblems.RunAsync(async () =>
                {
                    var asset = await assets.UpdateAsync(
                        id,
                        new UpdateAssetInput(
                            body.Class,
                            body.Name,
                            body.SerialNumber,
                            body.Manufacturer,
                            body.Model,
                            body.InstalledOn,
                            body.Latitude,
                            body.Longitude,
                            body.LocationNote),
                        cancellationToken);

                    return Results.Ok(AssetResponse.From(asset));
                }))
            .RequirePermission(Permissions.Assets.Write)
            .WithValidation<UpdateAssetRequest>()
            .WithName("UpdateAsset");

        // A status change is a transition, not a field edit, so it is its own POST sub-resource per
        // CONVENTIONS.md — and the aggregate refuses an illegal one with a 409 rather than a 400.
        group
            .MapPost("/{id:guid}/status", ([FromRoute] Guid id, ChangeAssetStatusRequest body, [FromServices] IAssetService assets, CancellationToken cancellationToken) =>
                AssetProblems.RunAsync(async () =>
                    Results.Ok(AssetResponse.From(await assets.ChangeStatusAsync(id, body.Status, body.Reason, cancellationToken)))))
            .RequirePermission(Permissions.Assets.Write)
            .WithValidation<ChangeAssetStatusRequest>()
            .WithName("ChangeAssetStatus");

        // Separate from the status route on purpose: grading plant and moving it through its
        // lifecycle are different jobs done by different people, and one body carrying both would
        // make "inspected, still in service" indistinguishable from a transition to itself.
        group
            .MapPost("/{id:guid}/condition", ([FromRoute] Guid id, AssessAssetConditionRequest body, [FromServices] IAssetService assets, CancellationToken cancellationToken) =>
                AssetProblems.RunAsync(async () =>
                    Results.Ok(AssetResponse.From(await assets.AssessConditionAsync(id, body.Condition, body.Note, cancellationToken)))))
            .RequirePermission(Permissions.Assets.Write)
            .WithValidation<AssessAssetConditionRequest>()
            .WithName("AssessAssetCondition");

        return endpoints;
    }
}
