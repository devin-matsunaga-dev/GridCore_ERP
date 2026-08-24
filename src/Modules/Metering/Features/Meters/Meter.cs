using GridCore.Modules.Metering.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Metering.Features.Meters;

/// <summary>
/// A revenue meter: the device that measures what a premise consumes, and therefore the thing every
/// reading, every consumption figure and every bill in GridCore is ultimately traceable to.
/// </summary>
/// <remarks>
/// <para>
/// A meter is fitted to a <b>service location</b>, never to a service account (owner's call, and
/// SPEC.md's wording). The service drop and the meter board are at the premise; they stay there
/// when the occupant moves out. An account number is <i>derived</i> context for a screen or a
/// bill — "the meter at the premise this account is served at" — and is deliberately not a column
/// here, because a meter that pointed at an account would have to be reassigned every time somebody
/// moved house, and the readings taken before the move would silently follow them.
/// </para>
/// <para>
/// Where the meter is and what status it is in are one fact, not two:
/// <see cref="ServiceLocationId"/> is set exactly when <see cref="MeterTransitions.IsFitted"/> is
/// true of <see cref="Status"/>. Every method here preserves that, which is what makes "one meter
/// per premise" a question the database can answer with a unique index.
/// </para>
/// <para>
/// There is deliberately no <c>Meter</c> class in the Assets register (WP-1.3): one device, one
/// record, so a bill dispute cannot find two of them disagreeing.
/// </para>
/// </remarks>
public sealed class Meter
{
    /// <summary>Longest manufacturer's serial number stored.</summary>
    public const int SerialNumberLength = 128;

    /// <summary>Longest manufacturer or model designation stored.</summary>
    public const int ModelLength = 128;

    /// <summary>Longest stored form of a type or status name.</summary>
    public const int EnumNameLength = 32;

    /// <summary>Longest reason recorded against a status change, an installation or a removal.</summary>
    public const int ReasonLength = MeterHistoryEntry.NoteLength;

    /// <summary>Most decimal places a dial reading may carry — the register's own width, not a rate.</summary>
    public const int DialDecimalPlaces = 3;

    /// <summary>Total digits stored for a dial reading.</summary>
    public const int DialPrecision = 18;

    private readonly List<MeterHistoryEntry> _history = [];

    private Meter()
    {
        // EF materialisation.
        MeterNumber = string.Empty;
        SerialNumber = string.Empty;
    }

    /// <summary>Identifier of this meter. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The number the utility knows it by, e.g. <c>MTR-000001</c>. Unique, and fixed at registration.</summary>
    public string MeterNumber { get; private init; }

    /// <summary>
    /// The manufacturer's serial number stamped on the meter. Required and unique across the
    /// register: unlike a pole or a span of conductor, every meter carries one, and it is what a
    /// crew reads off the device when the number plate is unreadable.
    /// </summary>
    public string SerialNumber { get; private set; }

    /// <summary>How the meter measures the service.</summary>
    public MeterType Type { get; private set; }

    /// <summary>Who made it.</summary>
    public string? Manufacturer { get; private set; }

    /// <summary>Their model designation.</summary>
    public string? Model { get; private set; }

    /// <summary>Where the meter stands in its working life.</summary>
    public MeterStatus Status { get; private set; }

    /// <summary>
    /// The premise this meter measures, or <see langword="null"/> when it is not fitted anywhere.
    /// A plain Guid with no foreign key — Customers is another module and another schema — checked
    /// through <c>IServiceLocationDirectory</c> before it is ever set.
    /// </summary>
    public Guid? ServiceLocationId { get; private set; }

    /// <summary>When the meter was last fitted at the premise it is on now.</summary>
    public DateTimeOffset? InstalledAt { get; private set; }

    /// <summary>
    /// What the dials read when the meter was last fitted. WP-2.2's first consumption figure at a
    /// premise is measured from this, so a meter that has been round the island and back does not
    /// bill its new customer for the last one's usage.
    /// </summary>
    public decimal? InstallationReading { get; private set; }

    /// <summary>When the meter was entered in the register.</summary>
    public DateTimeOffset RegisteredAt { get; private init; }

    /// <summary>When the status last moved.</summary>
    public DateTimeOffset? StatusChangedAt { get; private set; }

    /// <summary>Why it last moved.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>Everything that has happened to this meter, oldest first.</summary>
    public IReadOnlyList<MeterHistoryEntry> History => _history;

    /// <summary>Whether the meter is on a premise and measuring supply.</summary>
    public bool IsFitted => MeterTransitions.IsFitted(Status);

    /// <summary>Every status the machine allows from here — what a UI renders as transition buttons.</summary>
    public IReadOnlyList<MeterStatus> AllowedTransitions => MeterTransitions.AllowedFrom(Status);

    /// <summary>
    /// The statuses reachable through <c>POST /status</c> alone: the allowed moves that leave the
    /// meter where it is. Fitting and unfitting are <c>assign</c> and <c>remove</c>, so a UI that
    /// rendered <see cref="AllowedTransitions"/> as status buttons would offer two that always 409.
    /// </summary>
    public IReadOnlyList<MeterStatus> AllowedStatusChanges =>
        [.. MeterTransitions.AllowedFrom(Status).Where(next => !MeterTransitions.ChangesFitting(Status, next))];

    /// <summary>
    /// Enters a meter in the register under a number the caller has already reserved — see
    /// <see cref="IMeterNumberGenerator"/>. A meter is registered into a store, never onto a
    /// premise: fitting it is <see cref="InstallAt"/>, which is the act that records where and why.
    /// </summary>
    /// <exception cref="MeterValidationException">A required field is missing, or an enum value is undeclared.</exception>
    public static Meter Register(
        string meterNumber,
        string serialNumber,
        MeterType type,
        RegistryActor actor,
        DateTimeOffset now,
        string? manufacturer = null,
        string? model = null,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Require(meterNumber, nameof(meterNumber));
        Require(serialNumber, nameof(serialNumber));
        RequireDeclared(type);

        var meter = new Meter
        {
            Id = Guid.CreateVersion7(now),
            MeterNumber = RegistryText.Clean(meterNumber, RegistryNumbers.MaxLength)!,
            SerialNumber = RegistryText.Clean(serialNumber, SerialNumberLength)!,
            Type = type,
            Manufacturer = RegistryText.Clean(manufacturer, ModelLength),
            Model = RegistryText.Clean(model, ModelLength),

            // Always into stock. A meter that arrived already fitted is a data-migration problem,
            // not a registration — and admitting one here would let a premise be claimed without
            // the guards InstallAt runs.
            Status = MeterTransitions.Initial,
            RegisteredAt = now,
            StatusChangedAt = now,
        };

        // The opening line, so the history is complete from the first day rather than starting at
        // the first installation and leaving "where did this meter come from" unanswerable.
        meter._history.Add(MeterHistoryEntry.Registered(meter.Id, meter.Status, note, actor, now));

        return meter;
    }

    /// <summary>
    /// Corrects what is known about the device. Not the meter number, which is quoted on every bill
    /// raised from its readings; not the status or the premise, which are transitions rather than
    /// field edits.
    /// </summary>
    /// <exception cref="MeterValidationException">A required field is missing, or the type is undeclared.</exception>
    public void UpdateDetails(string serialNumber, MeterType type, string? manufacturer = null, string? model = null)
    {
        Require(serialNumber, nameof(serialNumber));
        RequireDeclared(type);

        SerialNumber = RegistryText.Clean(serialNumber, SerialNumberLength)!;
        Type = type;
        Manufacturer = RegistryText.Clean(manufacturer, ModelLength);
        Model = RegistryText.Clean(model, ModelLength);
    }

    /// <summary>
    /// Fits the meter at <paramref name="serviceLocationId"/>. The premise's existence and its
    /// active flag are the caller's to check — this module cannot see the Customers registry — but
    /// everything about the <i>meter</i> is checked here.
    /// </summary>
    /// <exception cref="MeterWorkflowException">The meter is not in stock, so it is not available to fit.</exception>
    /// <exception cref="MeterValidationException">The premise id is empty, or the installation reading is negative.</exception>
    public void InstallAt(
        Guid serviceLocationId,
        RegistryActor actor,
        DateTimeOffset now,
        decimal? installationReading = null,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // Every guard runs before the first mutation. WP-1.4 lost a test to the opposite ordering:
        // a refused write left the aggregate the caller still held describing something that never
        // happened, even though the transaction rolled the database back.
        if (serviceLocationId == Guid.Empty)
        {
            throw new MeterValidationException("A meter must be fitted at a service location; 'serviceLocationId' is empty.");
        }

        if (installationReading is < 0)
        {
            throw new MeterValidationException($"An installation reading cannot be negative; '{installationReading}' is.");
        }

        if (installationReading is { } reading && decimal.Round(reading, DialDecimalPlaces) != reading)
        {
            // Refused rather than rounded, exactly as WP-1.1 refuses a deposit finer than a cent:
            // CONVENTIONS.md's central rounding helper still has no home (WP-2.3 owns it), and the
            // column would have truncated a number nobody chose.
            throw new MeterValidationException(
                $"A meter reading is stored to {DialDecimalPlaces} decimal places; '{reading}' is finer than that.");
        }

        if (!MeterTransitions.IsAllowed(Status, MeterStatus.Installed))
        {
            // A 409, never a 400: whether this meter is available depends on where it is now, which
            // edge validation cannot see.
            throw new MeterWorkflowException(
                IsFitted
                    ? $"Meter {MeterNumber} is already fitted at premise {ServiceLocationId}; remove it before fitting it elsewhere."
                    : $"Meter {MeterNumber} is {Status} and cannot be fitted. Only a meter in stock can be.");
        }

        var from = Status;

        Status = MeterStatus.Installed;
        ServiceLocationId = serviceLocationId;
        InstalledAt = now;
        InstallationReading = installationReading;
        StatusChangedAt = now;
        StatusReason = RegistryText.Clean(note, ReasonLength);

        _history.Add(MeterHistoryEntry.Installed(Id, from, serviceLocationId, note, actor, now));
    }

    /// <summary>
    /// Takes the meter off the premise it is on. The premise is recorded on the history line before
    /// the meter lets go of it, which is what keeps "what measured this premise in March"
    /// answerable after an exchange.
    /// </summary>
    /// <exception cref="MeterWorkflowException">The meter is not fitted anywhere.</exception>
    public void Remove(RegistryActor actor, DateTimeOffset now, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!IsFitted || ServiceLocationId is not { } premise)
        {
            throw new MeterWorkflowException($"Meter {MeterNumber} is {Status} and is not fitted anywhere, so it cannot be removed.");
        }

        var from = Status;

        Status = MeterStatus.Removed;
        ServiceLocationId = null;
        InstalledAt = null;
        InstallationReading = null;
        StatusChangedAt = now;
        StatusReason = RegistryText.Clean(reason, ReasonLength);

        _history.Add(MeterHistoryEntry.Removed(Id, from, premise, reason, actor, now));
    }

    /// <summary>
    /// Moves the meter through the part of its lifecycle that does not change where it is —
    /// flagging a fitted meter faulty, passing it back into service, booking a removed one into
    /// stock, or retiring it.
    /// </summary>
    /// <exception cref="MeterWorkflowException">
    /// The move is not one <see cref="MeterTransitions"/> allows, or it would fit or unfit the
    /// meter, which is <see cref="InstallAt"/>'s and <see cref="Remove"/>'s work.
    /// </exception>
    public void ChangeStatus(MeterStatus status, RegistryActor actor, DateTimeOffset now, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        RequireDeclared(status);

        if (!MeterTransitions.IsAllowed(Status, status))
        {
            throw new MeterWorkflowException(
                Status == status
                    ? $"Meter {MeterNumber} is already {Status}."
                    : $"Meter {MeterNumber} cannot go from {Status} to {status}.");
        }

        if (MeterTransitions.ChangesFitting(Status, status))
        {
            // Refused rather than quietly done: a bare status change has no premise to fit to and
            // nowhere to record which premise a removal freed, so allowing it would leave a meter
            // marked installed at nowhere, or a premise still holding a meter nobody took off.
            throw new MeterWorkflowException(
                MeterTransitions.IsFitted(status)
                    ? $"Fitting meter {MeterNumber} needs the premise it goes to. Assign it instead."
                    : $"Taking meter {MeterNumber} off a premise is a removal, not a status change. Remove it instead.");
        }

        var from = Status;

        Status = status;
        StatusChangedAt = now;
        StatusReason = RegistryText.Clean(reason, ReasonLength);

        _history.Add(MeterHistoryEntry.StatusChanged(Id, from, status, ServiceLocationId, reason, actor, now));
    }

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MeterValidationException($"'{field}' is required to register a meter.");
        }
    }

    private static void RequireDeclared<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        // A value cast from an unmapped integer would be stored by name as a number and read back
        // as nothing anyone can act on.
        if (!Enum.IsDefined(value))
        {
            throw new MeterValidationException($"'{value}' is not a {typeof(TEnum).Name} GridCore declares.");
        }
    }
}
