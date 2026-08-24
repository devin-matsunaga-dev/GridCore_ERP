using GridCore.Modules.Assets.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Assets.Features.Assets;

/// <summary>
/// A piece of the utility's plant, on the register: what it is, where it stands, what state it is
/// in, and everything that has happened to it. The thing a work order is raised against, a cost is
/// booked to and an inspection is recorded on.
/// </summary>
/// <remarks>
/// Deliberately not attached to a service location. A premise belongs to the Customers module and
/// another module's rows are unreachable from here (ARCHITECTURE.md's boundary rule) — and most
/// plant does not stand at a premise anyway: a span of conductor crosses several, a substation
/// serves thousands, a bucket truck is wherever it was driven. Where an asset is, is
/// <see cref="Position"/> and <see cref="LocationNote"/>.
/// </remarks>
public sealed class Asset
{
    /// <summary>Longest asset name stored.</summary>
    public const int NameLength = 256;

    /// <summary>Longest manufacturer or model designation stored.</summary>
    public const int ModelLength = 128;

    /// <summary>Longest manufacturer's serial number stored.</summary>
    public const int SerialNumberLength = 128;

    /// <summary>Longest note about where the asset stands.</summary>
    public const int LocationNoteLength = 512;

    /// <summary>Longest stored form of a class, status or condition name.</summary>
    public const int EnumNameLength = 32;

    /// <summary>Longest reason recorded against a status change or an assessment.</summary>
    public const int ReasonLength = AssetHistoryEntry.NoteLength;

    private readonly List<AssetHistoryEntry> _history = [];

    private Asset()
    {
        // EF materialisation.
        AssetTag = string.Empty;
        Name = string.Empty;
    }

    /// <summary>Identifier of this asset. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The tag stencilled on the plant, e.g. <c>AST-000001</c>. Unique across assets, and fixed at registration.</summary>
    public string AssetTag { get; private init; }

    /// <summary>What kind of plant it is.</summary>
    public AssetClass Class { get; private set; }

    /// <summary>What it is called — "Songsong Substation Transformer T-3".</summary>
    public string Name { get; private set; }

    /// <summary>
    /// The manufacturer's serial number, where the plant carries one. Unique across the register
    /// when present: registering one physical transformer twice is the mistake this catches.
    /// </summary>
    public string? SerialNumber { get; private set; }

    /// <summary>Who made it.</summary>
    public string? Manufacturer { get; private set; }

    /// <summary>Their model designation.</summary>
    public string? Model { get; private set; }

    /// <summary>When it was installed, where that is known. Absent for plant that has never left the yard.</summary>
    public DateOnly? InstalledOn { get; private set; }

    /// <summary>Where the asset stands in its working life.</summary>
    public AssetStatus Status { get; private set; }

    /// <summary>How good a state it is in, as last assessed.</summary>
    public AssetCondition Condition { get; private set; }

    /// <summary>Degrees north, where anybody has recorded a position. Set only through <see cref="Position"/>.</summary>
    public decimal? Latitude { get; private set; }

    /// <summary>Degrees east, where anybody has recorded a position. Set only through <see cref="Position"/>.</summary>
    public decimal? Longitude { get; private set; }

    /// <summary>
    /// Where it physically is, where anybody has recorded that. Computed from the two columns
    /// rather than owned by EF: an owned struct is not something EF maps, and an owned <i>class</i>
    /// whose properties are both required cannot be optional in a shared table. Both-or-neither
    /// still holds, because the only way to set a position is to pass a <see cref="GeoPosition"/>.
    /// </summary>
    public GeoPosition? Position =>
        Latitude is { } latitude && Longitude is { } longitude ? new GeoPosition(latitude, longitude) : null;

    /// <summary>Where it is in a crew's words — "third pole past the church, seaward side".</summary>
    public string? LocationNote { get; private set; }

    /// <summary>When the asset was entered in the register.</summary>
    public DateTimeOffset RegisteredAt { get; private init; }

    /// <summary>When the status last moved.</summary>
    public DateTimeOffset? StatusChangedAt { get; private set; }

    /// <summary>Why it last moved.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>When the condition was last assessed. Absent while it is <see cref="AssetCondition.Unknown"/>.</summary>
    public DateTimeOffset? ConditionAssessedAt { get; private set; }

    /// <summary>Everything that has happened to this asset, oldest first.</summary>
    public IReadOnlyList<AssetHistoryEntry> History => _history;

    /// <summary>The statuses this asset may move to, for rendering transition buttons.</summary>
    public IReadOnlyList<AssetStatus> AllowedTransitions => AssetTransitions.AllowedFrom(Status);

    /// <summary>Whether this asset is still part of the network the utility operates and maintains.</summary>
    public bool IsOnTheBooks => AssetTransitions.IsOnTheBooks(Status);

    /// <summary>
    /// Enters an asset in the register under a tag the caller has already reserved — see
    /// <see cref="IAssetNumberGenerator"/>.
    /// </summary>
    /// <exception cref="AssetValidationException">A required field is missing, an enum is undeclared, or the install date is in the future.</exception>
    public static Asset Register(
        string assetTag,
        AssetClass assetClass,
        string name,
        RegistryActor actor,
        DateTimeOffset now,
        string? serialNumber = null,
        string? manufacturer = null,
        string? model = null,
        DateOnly? installedOn = null,
        GeoPosition? position = null,
        string? locationNote = null,
        AssetStatus status = AssetTransitions.Initial,
        AssetCondition condition = AssetCondition.Unknown,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Require(assetTag, nameof(assetTag));
        Require(name, nameof(name));
        RequireDeclared(assetClass);
        RequireDeclared(status);
        RequireDeclared(condition);

        var asset = new Asset
        {
            Id = Guid.CreateVersion7(now),
            AssetTag = RegistryText.Clean(assetTag, RegistryNumbers.MaxLength)!,
            Class = assetClass,
            Name = RegistryText.Clean(name, NameLength)!,
            SerialNumber = RegistryText.Clean(serialNumber, SerialNumberLength),
            Manufacturer = RegistryText.Clean(manufacturer, ModelLength),
            Model = RegistryText.Clean(model, ModelLength),
            InstalledOn = InstallDate(installedOn, now),
            Status = status,
            Condition = condition,
            Latitude = position?.Latitude,
            Longitude = position?.Longitude,
            LocationNote = RegistryText.Clean(locationNote, LocationNoteLength),
            RegisteredAt = now,
            StatusChangedAt = now,

            // Only stamped when somebody actually graded it. An asset registered Unknown has not
            // been assessed, and a date here would say an inspector had been and found nothing.
            ConditionAssessedAt = condition is AssetCondition.Unknown ? null : now,
        };

        // The opening line, so the history is complete from the first day rather than starting at
        // the first transition and leaving "where did this asset come from" unanswerable.
        asset._history.Add(AssetHistoryEntry.Registered(asset.Id, status, condition, note, actor, now));

        return asset;
    }

    /// <summary>
    /// Corrects the details of an asset. The tag is not among them: it is stencilled on the plant
    /// and quoted by every work order raised against it, so it is fixed at registration. Neither
    /// are the status and the condition — those are transitions and assessments, not field edits.
    /// </summary>
    /// <exception cref="AssetValidationException">A required field is missing, the class is undeclared, or the install date is in the future.</exception>
    public void UpdateDetails(
        AssetClass assetClass,
        string name,
        DateTimeOffset now,
        string? serialNumber = null,
        string? manufacturer = null,
        string? model = null,
        DateOnly? installedOn = null,
        GeoPosition? position = null,
        string? locationNote = null)
    {
        Require(name, nameof(name));
        RequireDeclared(assetClass);

        // Every guard runs before the first assignment, so a rejected correction leaves the asset
        // exactly as it was rather than half-applied.
        var installed = InstallDate(installedOn, now);

        Class = assetClass;
        Name = RegistryText.Clean(name, NameLength)!;
        SerialNumber = RegistryText.Clean(serialNumber, SerialNumberLength);
        Manufacturer = RegistryText.Clean(manufacturer, ModelLength);
        Model = RegistryText.Clean(model, ModelLength);
        InstalledOn = installed;
        Latitude = position?.Latitude;
        Longitude = position?.Longitude;
        LocationNote = RegistryText.Clean(locationNote, LocationNoteLength);
    }

    /// <summary>Moves the asset to <paramref name="status"/>, appending the line that says why.</summary>
    /// <exception cref="AssetWorkflowException">The move is not one <see cref="AssetTransitions"/> allows.</exception>
    public void ChangeStatus(AssetStatus status, RegistryActor actor, DateTimeOffset now, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        RequireDeclared(status);

        if (!AssetTransitions.IsAllowed(Status, status))
        {
            // A 409, never a 400: whether this move is legal depends on where the asset is now,
            // which edge validation cannot see.
            throw new AssetWorkflowException(
                Status == status
                    ? $"Asset {AssetTag} is already {Status}."
                    : $"Asset {AssetTag} cannot go from {Status} to {status}.");
        }

        var from = Status;

        Status = status;
        StatusChangedAt = now;
        StatusReason = RegistryText.Clean(reason, ReasonLength);

        _history.Add(AssetHistoryEntry.StatusChanged(Id, from, status, reason, actor, now));
    }

    /// <summary>
    /// Records an inspector's grading. Any grade may follow any other — plant is repaired and plant
    /// weathers storms — so this is not guarded the way a status move is.
    /// </summary>
    /// <exception cref="AssetValidationException">The condition is not one GridCore declares.</exception>
    public void AssessCondition(AssetCondition condition, RegistryActor actor, DateTimeOffset now, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        RequireDeclared(condition);

        var from = Condition;

        Condition = condition;
        ConditionAssessedAt = now;

        // Recorded even when the grade is unchanged: "inspected, still Fair" is the finding a
        // maintenance plan is built on, and dropping it would make an inspected asset
        // indistinguishable from one nobody has looked at since last year.
        _history.Add(AssetHistoryEntry.ConditionAssessed(Id, from, condition, note, actor, now));
    }

    /// <summary>
    /// Records work done on this asset under a work order — the maintenance half of the history.
    /// </summary>
    /// <remarks>
    /// Nothing calls this yet. WP-3.4's work-order consumer does, when a job completes; it is here
    /// now because the read model it fills is WP-1.3's to ship, and a read model with no writer is
    /// a table nobody can prove works.
    /// </remarks>
    /// <exception cref="AssetValidationException">The asset has been retired.</exception>
    public void RecordMaintenance(Guid? workOrderId, string summary, RegistryActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Require(summary, nameof(summary));

        if (Status is AssetStatus.Retired)
        {
            // Retired is terminal for a reason: work booked against scrapped plant is work booked
            // against the wrong asset, and the cost would land on a job nobody can go and look at.
            throw new AssetValidationException($"Asset {AssetTag} is retired; maintenance cannot be recorded against it.");
        }

        _history.Add(AssetHistoryEntry.Maintenance(Id, workOrderId, summary, actor, now));
    }

    private static DateOnly? InstallDate(DateOnly? installedOn, DateTimeOffset now)
    {
        if (installedOn is { } date && date > DateOnly.FromDateTime(now.UtcDateTime))
        {
            // A register records what exists. A future install date is a typo, or a planned job —
            // and a planned job is a work order, not an asset record.
            throw new AssetValidationException($"An install date cannot be in the future; '{date:O}' is.");
        }

        return installedOn;
    }

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AssetValidationException($"'{field}' is required to register an asset.");
        }
    }

    private static void RequireDeclared<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        // A value cast from an unmapped integer would be stored by name as a number and read back
        // as nothing anyone can act on.
        if (!Enum.IsDefined(value))
        {
            throw new AssetValidationException($"'{value}' is not a {typeof(TEnum).Name} GridCore declares.");
        }
    }
}
