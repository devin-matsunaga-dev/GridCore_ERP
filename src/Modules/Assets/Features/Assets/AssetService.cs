using GridCore.Contracts.Events;
using GridCore.Modules.Assets.Data;
using GridCore.Modules.Assets.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Assets.Features.Assets;

/// <summary>The details of an asset a caller may set or correct. Shared by registration and update.</summary>
public interface IAssetDetails
{
    /// <summary>What kind of plant it is.</summary>
    AssetClass Class { get; }

    /// <summary>What it is called.</summary>
    string Name { get; }

    /// <summary>The manufacturer's serial number, where the plant carries one.</summary>
    string? SerialNumber { get; }

    /// <summary>Who made it.</summary>
    string? Manufacturer { get; }

    /// <summary>Their model designation.</summary>
    string? Model { get; }

    /// <summary>When it was installed, where that is known.</summary>
    DateOnly? InstalledOn { get; }

    /// <summary>Degrees north, where anybody has recorded a position.</summary>
    decimal? Latitude { get; }

    /// <summary>Degrees east, where anybody has recorded a position.</summary>
    decimal? Longitude { get; }

    /// <summary>Where it is in a crew's words.</summary>
    string? LocationNote { get; }
}

/// <summary>What a caller supplies to enter an asset in the register.</summary>
/// <param name="Class">What kind of plant it is.</param>
/// <param name="Name">What it is called.</param>
/// <param name="SerialNumber">The manufacturer's serial number.</param>
/// <param name="Manufacturer">Who made it.</param>
/// <param name="Model">Their model designation.</param>
/// <param name="InstalledOn">When it was installed.</param>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
/// <param name="LocationNote">Where it is, in a crew's words.</param>
/// <param name="Status">Where it starts. Most plant is received into storage.</param>
/// <param name="Condition">How it was graded on arrival, if anybody looked.</param>
/// <param name="Note">Why it is being registered, for the history.</param>
public sealed record RegisterAssetInput(
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

/// <summary>What a caller supplies to correct an asset's details.</summary>
/// <param name="Class">What kind of plant it is.</param>
/// <param name="Name">What it is called.</param>
/// <param name="SerialNumber">The manufacturer's serial number.</param>
/// <param name="Manufacturer">Who made it.</param>
/// <param name="Model">Their model designation.</param>
/// <param name="InstalledOn">When it was installed.</param>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
/// <param name="LocationNote">Where it is, in a crew's words.</param>
public sealed record UpdateAssetInput(
    AssetClass Class,
    string Name,
    string? SerialNumber = null,
    string? Manufacturer = null,
    string? Model = null,
    DateOnly? InstalledOn = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    string? LocationNote = null) : IAssetDetails;

/// <summary>How the asset list is filtered.</summary>
/// <param name="Search">Matched against the tag, the name and the serial number, case-insensitively.</param>
/// <param name="Class">Only assets of this kind.</param>
/// <param name="Status">Only assets in this status.</param>
/// <param name="Condition">Only assets graded this way — the maintenance-plan query.</param>
/// <param name="Limit">Most rows to return.</param>
public sealed record AssetQuery(
    string? Search = null,
    AssetClass? Class = null,
    AssetStatus? Status = null,
    AssetCondition? Condition = null,
    int Limit = 50);

/// <summary>The asset register. Endpoints are a thin layer over it.</summary>
public interface IAssetService
{
    /// <summary>Enters an asset in the register, issuing the next asset tag.</summary>
    Task<Asset> RegisterAsync(RegisterAssetInput input, CancellationToken cancellationToken = default);

    /// <summary>Corrects an asset's details. Not its tag, its status or its condition.</summary>
    Task<Asset> UpdateAsync(Guid id, UpdateAssetInput input, CancellationToken cancellationToken = default);

    /// <summary>Moves an asset through its lifecycle.</summary>
    Task<Asset> ChangeStatusAsync(Guid id, AssetStatus status, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Records an inspector's grading of an asset's condition.</summary>
    Task<Asset> AssessConditionAsync(Guid id, AssetCondition condition, string? note, CancellationToken cancellationToken = default);

    /// <summary>One asset with its history, or <see langword="null"/> if there is no such id.</summary>
    Task<Asset?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The asset list, newest first.</summary>
    Task<IReadOnlyList<Asset>> ListAsync(AssetQuery query, CancellationToken cancellationToken = default);

    /// <summary>One asset's history, oldest first, optionally narrowed to one kind of line.</summary>
    /// <exception cref="AssetNotFoundException">There is no asset with that id.</exception>
    Task<IReadOnlyList<AssetHistoryEntry>> HistoryAsync(
        Guid id,
        AssetHistoryEntryType? entryType = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The asset register over the assets schema.</summary>
/// <remarks>
/// Every write runs inside <see cref="IUnitOfWork.ExecuteAsync"/> and never calls
/// <c>SaveChanges</c> itself, so the asset row, its history line, its audit entry and its outbox
/// row are one transaction — invariants 1 and 2. The history line is written by the aggregate
/// rather than here, which is what makes "the status moved but nothing recorded why" impossible.
/// </remarks>
public sealed class AssetService(
    AssetsDbContext database,
    IAssetNumberGenerator tags,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    IEventPublisher events,
    ICurrentUser currentUser,
    TimeProvider clock) : IAssetService
{
    /// <summary>The largest page <see cref="ListAsync"/> will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public Task<Asset> RegisterAsync(RegisterAssetInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                await RequireSerialIsFreeAsync(input.SerialNumber, excluding: null, ct).ConfigureAwait(false);

                var assetTag = await tags.NextAssetTagAsync(ct).ConfigureAwait(false);

                // The unique index is the real guarantee; this turns the loser of a race into a 409
                // the caller can retry rather than a 500 out of the database.
                if (await database.Assets.AnyAsync(existing => existing.AssetTag == assetTag, ct).ConfigureAwait(false))
                {
                    throw new AssetWorkflowException(
                        $"Asset tag {assetTag} has just been taken by another registration. Try again.");
                }

                var asset = Asset.Register(
                    assetTag,
                    input.Class,
                    input.Name,
                    RegistryActor.Of(currentUser),
                    now,
                    input.SerialNumber,
                    input.Manufacturer,
                    input.Model,
                    input.InstalledOn,
                    GeoPosition.From(input.Latitude, input.Longitude),
                    input.LocationNote,
                    input.Status,
                    input.Condition,
                    input.Note);

                database.Assets.Add(asset);

                audit.Record(
                    AuditActions.AssetRegistered,
                    AuditEntityTypes.Asset,
                    asset.Id.ToString(),
                    before: null,
                    after: AssetSnapshot.Of(asset));

                await events.PublishAsync(
                    AssetRegistered.For(
                        now,
                        asset.Id,
                        asset.AssetTag,
                        asset.Class.ToString(),
                        asset.Status.ToString(),
                        asset.Condition.ToString()),
                    ct).ConfigureAwait(false);

                return asset;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Asset> UpdateAsync(Guid id, UpdateAssetInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var asset = await LoadAsync(id, ct).ConfigureAwait(false);
                var before = AssetSnapshot.Of(asset);

                await RequireSerialIsFreeAsync(input.SerialNumber, excluding: asset.Id, ct).ConfigureAwait(false);

                asset.UpdateDetails(
                    input.Class,
                    input.Name,
                    now,
                    input.SerialNumber,
                    input.Manufacturer,
                    input.Model,
                    input.InstalledOn,
                    GeoPosition.From(input.Latitude, input.Longitude),
                    input.LocationNote);

                audit.Record(AuditActions.AssetUpdated, AuditEntityTypes.Asset, asset.Id.ToString(), before, AssetSnapshot.Of(asset));

                // No event: correcting a model designation or a typo in a name is not a fact another
                // module acts on, and publishing one would put noise in every consumer's inbox.
                return asset;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Asset> ChangeStatusAsync(Guid id, AssetStatus status, string? reason, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var asset = await LoadAsync(id, ct).ConfigureAwait(false);
                var before = AssetSnapshot.Of(asset);
                var from = asset.Status;

                asset.ChangeStatus(status, RegistryActor.Of(currentUser), now, reason);

                audit.Record(AuditActions.AssetStatusChanged, AuditEntityTypes.Asset, asset.Id.ToString(), before, AssetSnapshot.Of(asset));

                await events.PublishAsync(
                    AssetStatusChanged.For(now, asset.Id, asset.AssetTag, from.ToString(), asset.Status.ToString(), reason),
                    ct).ConfigureAwait(false);

                return asset;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<Asset> AssessConditionAsync(Guid id, AssetCondition condition, string? note, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var asset = await LoadAsync(id, ct).ConfigureAwait(false);
                var before = AssetSnapshot.Of(asset);

                asset.AssessCondition(condition, RegistryActor.Of(currentUser), now, note);

                audit.Record(AuditActions.AssetConditionAssessed, AuditEntityTypes.Asset, asset.Id.ToString(), before, AssetSnapshot.Of(asset));

                // No event, deliberately: a condition is this module's own assessment and it is
                // revised at every inspection, where a status is the fact other modules gate on.
                return asset;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<Asset?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Assets
            .Include(asset => asset.History)
            .FirstOrDefaultAsync(asset => asset.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Asset>> ListAsync(AssetQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No Include: a list row shows what an asset is and where it stands, not everything that has
        // happened to it. The history is one more request away, on the asset being looked at.
        var assets = database.Assets.AsNoTracking();

        // Matched against non-nullable locals: the columns are stored by name, and EF cannot
        // translate a nullable-to-converted-value comparison.
        if (query.Class is { } assetClass)
        {
            assets = assets.Where(asset => asset.Class == assetClass);
        }

        if (query.Status is { } status)
        {
            assets = assets.Where(asset => asset.Status == status);
        }

        if (query.Condition is { } condition)
        {
            assets = assets.Where(asset => asset.Condition == condition);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Lower-cased on both sides rather than ILIKE, so the fast tier exercises the same SQL
            // shape production runs. A crew searches by whatever is legible on the plate — the
            // stencilled tag, the name on the drawing, or the manufacturer's serial.
            var term = query.Search.Trim().ToLowerInvariant();

            assets = assets.Where(asset =>
                asset.AssetTag.ToLower().Contains(term)
                || asset.Name.ToLower().Contains(term)
                || (asset.SerialNumber != null && asset.SerialNumber.ToLower().Contains(term)));
        }

        // Ordered by key: ids are Guid v7, so the primary-key index already orders chronologically
        // on Postgres and on the fast tier's SQLite alike.
        return await assets
            .OrderByDescending(asset => asset.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssetHistoryEntry>> HistoryAsync(
        Guid id,
        AssetHistoryEntryType? entryType = null,
        CancellationToken cancellationToken = default)
    {
        if (!await database.Assets.AnyAsync(asset => asset.Id == id, cancellationToken).ConfigureAwait(false))
        {
            // Distinguished from an asset that simply has no lines, which cannot happen — every
            // asset is registered with one — but an empty list for a missing id would say it had.
            throw new AssetNotFoundException(id);
        }

        var history = database.AssetHistory
            .AsNoTracking()
            .Where(entry => entry.AssetId == id);

        if (entryType is { } kind)
        {
            history = history.Where(entry => entry.EntryType == kind);
        }

        return await history
            .OrderBy(entry => entry.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Asset> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await database.Assets
            .Include(asset => asset.History)
            .FirstOrDefaultAsync(asset => asset.Id == id, cancellationToken).ConfigureAwait(false)
        ?? throw new AssetNotFoundException(id);

    private async Task RequireSerialIsFreeAsync(string? serialNumber, Guid? excluding, CancellationToken cancellationToken)
    {
        var serial = RegistryText.Clean(serialNumber, Asset.SerialNumberLength);

        if (serial is null)
        {
            // Plenty of plant carries no serial — a pole, a span of conductor — and the unique index
            // treats NULLs as distinct, so any number of them coexist.
            return;
        }

        var taken = await database.Assets
            .Where(existing => existing.SerialNumber == serial)
            .Where(existing => excluding == null || existing.Id != excluding)
            .Select(existing => existing.AssetTag)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (taken is not null)
        {
            // The unique index is what actually guarantees this; the check is here so the second
            // registration of one physical transformer reads as a conflict naming the asset it
            // collides with, rather than a 500.
            throw new AssetWorkflowException(
                $"Serial number '{serial}' is already registered as asset {taken}.");
        }
    }
}

/// <summary>
/// The before/after shape an asset is audited as. A dedicated record rather than the entity, so
/// changing the entity later cannot silently change the meaning of historic entries.
/// </summary>
/// <param name="Id">Which asset.</param>
/// <param name="AssetTag">Its tag.</param>
/// <param name="Class">What kind of plant it is.</param>
/// <param name="Name">What it is called.</param>
/// <param name="SerialNumber">The manufacturer's serial number.</param>
/// <param name="Manufacturer">Who made it.</param>
/// <param name="Model">Their model designation.</param>
/// <param name="InstalledOn">When it was installed.</param>
/// <param name="Status">Where it stands in its working life.</param>
/// <param name="Condition">How it is graded.</param>
/// <param name="Latitude">Degrees north, where a position is recorded.</param>
/// <param name="Longitude">Degrees east, where a position is recorded.</param>
/// <param name="LocationNote">Where it is, in a crew's words.</param>
/// <param name="StatusReason">Why the status last moved.</param>
public sealed record AssetSnapshot(
    Guid Id,
    string AssetTag,
    AssetClass Class,
    string Name,
    string? SerialNumber,
    string? Manufacturer,
    string? Model,
    DateOnly? InstalledOn,
    AssetStatus Status,
    AssetCondition Condition,
    decimal? Latitude,
    decimal? Longitude,
    string? LocationNote,
    string? StatusReason)
{
    /// <summary>Takes a snapshot of <paramref name="asset"/> as it stands.</summary>
    public static AssetSnapshot Of(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        return new AssetSnapshot(
            asset.Id,
            asset.AssetTag,
            asset.Class,
            asset.Name,
            asset.SerialNumber,
            asset.Manufacturer,
            asset.Model,
            asset.InstalledOn,
            asset.Status,
            asset.Condition,
            asset.Position?.Latitude,
            asset.Position?.Longitude,
            asset.LocationNote,
            asset.StatusReason);
    }
}
