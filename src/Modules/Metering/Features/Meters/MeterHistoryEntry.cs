using GridCore.Modules.Metering.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Metering.Features.Meters;

/// <summary>What a <see cref="MeterHistoryEntry"/> records.</summary>
public enum MeterHistoryEntryType
{
    /// <summary>The meter was entered in the register. The opening line of every history.</summary>
    Registered = 1,

    /// <summary>The meter was fitted at a premise.</summary>
    Installed = 2,

    /// <summary>The meter was taken off a premise.</summary>
    Removed = 3,

    /// <summary>The meter moved through its lifecycle without changing where it is.</summary>
    StatusChanged = 4,
}

/// <summary>
/// One line of a meter's history: what happened to it, when, where, and who says so. Append-only —
/// a mistake is corrected by the next line rather than by editing the last one.
/// </summary>
/// <remarks>
/// <para>
/// This is where "which meter was measuring this premise in March" is answered. The meter row
/// itself carries only the premise it is on <i>now</i> — a removal clears it — so without this
/// table a bill dispute over a period after an exchange would have nothing to read. That is also
/// why an installation and a removal each stamp their own
/// <see cref="ServiceLocationId"/>: the line has to say which premise, not point at whatever the
/// meter happens to be fitted to today.
/// </para>
/// <para>
/// Deliberately not a replacement for the audit trail. The audit entry (invariant 1) is the
/// administrative record of a write; this is the service record of the device. Both commit in the
/// same transaction, so neither can exist without the other.
/// </para>
/// </remarks>
public sealed class MeterHistoryEntry
{
    /// <summary>Longest note recorded against a history line.</summary>
    public const int NoteLength = 1024;

    private MeterHistoryEntry()
    {
        // EF materialisation.
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this entry. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The meter this line belongs to.</summary>
    public Guid MeterId { get; private init; }

    /// <summary>What kind of thing happened.</summary>
    public MeterHistoryEntryType EntryType { get; private init; }

    /// <summary>Where the meter was, in its lifecycle. <see langword="null"/> on the opening line — it came from nowhere.</summary>
    public MeterStatus? FromStatus { get; private init; }

    /// <summary>Where it went.</summary>
    public MeterStatus ToStatus { get; private init; }

    /// <summary>
    /// The premise involved, on an installation or a removal line. A plain Guid with no foreign
    /// key: Customers is another module and another schema, so the database cannot enforce it and
    /// this module must never query that table (ARCHITECTURE.md's boundary rule). The premise is
    /// checked through <c>IServiceLocationDirectory</c> before the line is ever written.
    /// </summary>
    public Guid? ServiceLocationId { get; private init; }

    /// <summary>Why, or what was done, in the operator's words.</summary>
    public string? Note { get; private init; }

    /// <summary>Subject id of whoever did it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>Records the opening line, written when the meter is registered.</summary>
    internal static MeterHistoryEntry Registered(
        Guid meterId,
        MeterStatus status,
        string? note,
        RegistryActor actor,
        DateTimeOffset now) =>
        Line(meterId, MeterHistoryEntryType.Registered, status, actor, now, note);

    /// <summary>Records a meter being fitted at a premise.</summary>
    internal static MeterHistoryEntry Installed(
        Guid meterId,
        MeterStatus from,
        Guid serviceLocationId,
        string? note,
        RegistryActor actor,
        DateTimeOffset now) =>
        Line(meterId, MeterHistoryEntryType.Installed, MeterStatus.Installed, actor, now, note, from, serviceLocationId);

    /// <summary>Records a meter coming off a premise.</summary>
    internal static MeterHistoryEntry Removed(
        Guid meterId,
        MeterStatus from,
        Guid serviceLocationId,
        string? note,
        RegistryActor actor,
        DateTimeOffset now) =>
        Line(meterId, MeterHistoryEntryType.Removed, MeterStatus.Removed, actor, now, note, from, serviceLocationId);

    /// <summary>Records a lifecycle move that left the meter where it was.</summary>
    internal static MeterHistoryEntry StatusChanged(
        Guid meterId,
        MeterStatus from,
        MeterStatus to,
        Guid? serviceLocationId,
        string? note,
        RegistryActor actor,
        DateTimeOffset now) =>
        Line(meterId, MeterHistoryEntryType.StatusChanged, to, actor, now, note, from, serviceLocationId);

    private static MeterHistoryEntry Line(
        Guid meterId,
        MeterHistoryEntryType entryType,
        MeterStatus toStatus,
        RegistryActor actor,
        DateTimeOffset now,
        string? note,
        MeterStatus? fromStatus = null,
        Guid? serviceLocationId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return new MeterHistoryEntry
        {
            Id = Guid.CreateVersion7(now),
            MeterId = meterId,
            EntryType = entryType,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ServiceLocationId = serviceLocationId,
            Note = RegistryText.Clean(note, NoteLength),
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new MeterValidationException("A meter history entry must name who made the change."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            RecordedAt = now,
        };
    }
}
