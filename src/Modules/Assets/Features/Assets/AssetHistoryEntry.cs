using GridCore.Modules.Assets.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Assets.Features.Assets;

/// <summary>What an <see cref="AssetHistoryEntry"/> records.</summary>
public enum AssetHistoryEntryType
{
    /// <summary>The asset was entered in the register. The opening line of every history.</summary>
    Registered = 1,

    /// <summary>The asset moved through its lifecycle.</summary>
    StatusChanged = 2,

    /// <summary>An inspector graded the asset's condition.</summary>
    ConditionAssessed = 3,

    /// <summary>
    /// Work was done on the asset. Written when a work order completes — WP-3.4's, and the reason
    /// this table exists in WP-1.3 rather than later.
    /// </summary>
    Maintenance = 4,
}

/// <summary>
/// One line of an asset's history: what happened to it, when, and who says so. Append-only — a
/// history is a record of what happened, so a mistake is corrected by the next line rather than by
/// editing the last one.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>maintenance-history read model</b> WP-1.3 owes, and it is deliberately one table
/// rather than two. A technician standing in front of a transformer wants one timeline — installed,
/// inspected Fair, repaired under WO-114, withdrawn — and splitting lifecycle from maintenance
/// would mean interleaving two queries on screen to reconstruct it. The lifecycle lines are written
/// by <see cref="Asset"/> today; the <see cref="AssetHistoryEntryType.Maintenance"/> lines are
/// written by <see cref="Asset.RecordMaintenance"/>, whose caller is WP-3.4's work-order consumer.
/// </para>
/// <para>
/// Deliberately not a replacement for the audit trail. The audit entry (invariant 1) is the
/// tamper-evident administrative record of a write, held in the platform schema and filtered by
/// action; this is the engineering record of the plant itself. They answer different questions and
/// are written in the same transaction, so neither can exist without the other.
/// </para>
/// </remarks>
public sealed class AssetHistoryEntry
{
    /// <summary>Longest note recorded against a history line.</summary>
    public const int NoteLength = 1024;

    private AssetHistoryEntry()
    {
        // EF materialisation.
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this entry. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The asset this line belongs to.</summary>
    public Guid AssetId { get; private init; }

    /// <summary>What kind of thing happened.</summary>
    public AssetHistoryEntryType EntryType { get; private init; }

    /// <summary>Where the asset was, on a lifecycle line. <see langword="null"/> on the opening line — it came from nowhere.</summary>
    public AssetStatus? FromStatus { get; private init; }

    /// <summary>Where the asset went, on a lifecycle line.</summary>
    public AssetStatus? ToStatus { get; private init; }

    /// <summary>How the asset was graded before, on an assessment line.</summary>
    public AssetCondition? FromCondition { get; private init; }

    /// <summary>How it is graded now, on an assessment line.</summary>
    public AssetCondition? ToCondition { get; private init; }

    /// <summary>Why, or what was done, in the operator's words.</summary>
    public string? Note { get; private init; }

    /// <summary>
    /// The work order the maintenance was done under, on a maintenance line. A plain Guid with no
    /// foreign key: Work Orders is another module and another schema, so the database cannot
    /// enforce it and this module must never query that table (ARCHITECTURE.md's boundary rule).
    /// </summary>
    public Guid? WorkOrderId { get; private init; }

    /// <summary>Subject id of whoever did it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>Records the opening line, written when the asset is registered.</summary>
    internal static AssetHistoryEntry Registered(
        Guid assetId,
        AssetStatus status,
        AssetCondition condition,
        string? note,
        RegistryActor actor,
        DateTimeOffset now) =>
        Line(assetId, AssetHistoryEntryType.Registered, actor, now, note, toStatus: status, toCondition: condition);

    /// <summary>Records a lifecycle move.</summary>
    internal static AssetHistoryEntry StatusChanged(
        Guid assetId,
        AssetStatus from,
        AssetStatus to,
        string? note,
        RegistryActor actor,
        DateTimeOffset now) =>
        Line(assetId, AssetHistoryEntryType.StatusChanged, actor, now, note, fromStatus: from, toStatus: to);

    /// <summary>Records an inspector's grading.</summary>
    internal static AssetHistoryEntry ConditionAssessed(
        Guid assetId,
        AssetCondition from,
        AssetCondition to,
        string? note,
        RegistryActor actor,
        DateTimeOffset now) =>
        Line(assetId, AssetHistoryEntryType.ConditionAssessed, actor, now, note, fromCondition: from, toCondition: to);

    /// <summary>Records work done under a work order. WP-3.4's line.</summary>
    internal static AssetHistoryEntry Maintenance(
        Guid assetId,
        Guid? workOrderId,
        string? note,
        RegistryActor actor,
        DateTimeOffset now) =>
        Line(assetId, AssetHistoryEntryType.Maintenance, actor, now, note, workOrderId: workOrderId);

    private static AssetHistoryEntry Line(
        Guid assetId,
        AssetHistoryEntryType entryType,
        RegistryActor actor,
        DateTimeOffset now,
        string? note,
        AssetStatus? fromStatus = null,
        AssetStatus? toStatus = null,
        AssetCondition? fromCondition = null,
        AssetCondition? toCondition = null,
        Guid? workOrderId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return new AssetHistoryEntry
        {
            Id = Guid.CreateVersion7(now),
            AssetId = assetId,
            EntryType = entryType,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            FromCondition = fromCondition,
            ToCondition = toCondition,
            WorkOrderId = workOrderId,
            Note = RegistryText.Clean(note, NoteLength),
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new AssetValidationException("An asset history entry must name who made the change."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            RecordedAt = now,
        };
    }
}
