using GridCore.Contracts.Directories;
using GridCore.Contracts.Events;
using GridCore.Modules.Metering.Data;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Metering.Features.Meters;

/// <summary>The device details a caller may set or correct. Shared by registration and update.</summary>
public interface IMeterDetails
{
    /// <summary>The manufacturer's serial number stamped on the meter.</summary>
    string SerialNumber { get; }

    /// <summary>How the meter measures the service.</summary>
    MeterType Type { get; }

    /// <summary>How many whole digits its register carries, before the dials roll back to zero.</summary>
    int RegisterDigits { get; }

    /// <summary>Who made it.</summary>
    string? Manufacturer { get; }

    /// <summary>Their model designation.</summary>
    string? Model { get; }
}

/// <summary>What a caller supplies to enter a meter in the register.</summary>
/// <param name="SerialNumber">The manufacturer's serial number.</param>
/// <param name="Type">How the meter measures the service.</param>
/// <param name="RegisterDigits">How many whole digits its register carries.</param>
/// <param name="Manufacturer">Who made it.</param>
/// <param name="Model">Their model designation.</param>
/// <param name="Note">Why it is being registered, for the history.</param>
public sealed record RegisterMeterInput(
    string SerialNumber,
    MeterType Type,
    int RegisterDigits = Meter.DefaultRegisterDigits,
    string? Manufacturer = null,
    string? Model = null,
    string? Note = null) : IMeterDetails;

/// <summary>What a caller supplies to correct a meter's device details.</summary>
/// <param name="SerialNumber">The manufacturer's serial number.</param>
/// <param name="Type">How the meter measures the service.</param>
/// <param name="RegisterDigits">How many whole digits its register carries.</param>
/// <param name="Manufacturer">Who made it.</param>
/// <param name="Model">Their model designation.</param>
public sealed record UpdateMeterInput(
    string SerialNumber,
    MeterType Type,
    int RegisterDigits = Meter.DefaultRegisterDigits,
    string? Manufacturer = null,
    string? Model = null) : IMeterDetails;

/// <summary>What a caller supplies to fit a meter at a premise.</summary>
/// <param name="ServiceLocationId">The premise it goes to.</param>
/// <param name="InstallationReading">What the dials read as it went on, where the crew recorded it.</param>
/// <param name="Note">Why, for the history and the audit trail.</param>
public sealed record AssignMeterInput(
    Guid ServiceLocationId,
    decimal? InstallationReading = null,
    string? Note = null);

/// <summary>How the meter list is filtered.</summary>
/// <param name="Search">Matched against the meter number and the serial number, case-insensitively.</param>
/// <param name="Type">Only meters of this kind.</param>
/// <param name="Status">Only meters in this status.</param>
/// <param name="ServiceLocationId">Only the meter fitted at this premise — the 360° page's query.</param>
/// <param name="Fitted">
/// <see langword="true"/> for meters on a premise, <see langword="false"/> for everything in a
/// store or scrapped. The store's "what can I issue" question, without naming three statuses.
/// </param>
/// <param name="Limit">Most rows to return.</param>
public sealed record MeterQuery(
    string? Search = null,
    MeterType? Type = null,
    MeterStatus? Status = null,
    Guid? ServiceLocationId = null,
    bool? Fitted = null,
    int Limit = 50);

/// <summary>
/// A meter together with the premise it is fitted at, as the register hands it out.
/// </summary>
/// <remarks>
/// The premise is resolved through <see cref="IServiceLocationDirectory"/> — never a join, because
/// it is another module's row. It is <see langword="null"/> for a meter in a store, and also for a
/// fitted meter whose premise the directory could not resolve, which is what a caller sees if the
/// two registries ever disagree. A screen renders the id in that case rather than losing the row.
/// </remarks>
/// <param name="Meter">The meter itself.</param>
/// <param name="ServiceLocation">Where it is fitted, where it is fitted anywhere.</param>
public sealed record MeterRecord(Meter Meter, ServiceLocationSummary? ServiceLocation);

/// <summary>The meter register. Endpoints are a thin layer over it.</summary>
public interface IMeterService
{
    /// <summary>Enters a meter in the register, issuing the next meter number. It starts in stock.</summary>
    Task<MeterRecord> RegisterAsync(RegisterMeterInput input, CancellationToken cancellationToken = default);

    /// <summary>Corrects a meter's device details. Not its number, its status or where it is fitted.</summary>
    Task<MeterRecord> UpdateAsync(Guid id, UpdateMeterInput input, CancellationToken cancellationToken = default);

    /// <summary>Fits a meter at a premise.</summary>
    Task<MeterRecord> AssignAsync(Guid id, AssignMeterInput input, CancellationToken cancellationToken = default);

    /// <summary>Takes a meter off the premise it is on.</summary>
    Task<MeterRecord> RemoveAsync(Guid id, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Moves a meter through the part of its lifecycle that does not change where it is.</summary>
    Task<MeterRecord> ChangeStatusAsync(Guid id, MeterStatus status, string? reason, CancellationToken cancellationToken = default);

    /// <summary>One meter with its history, or <see langword="null"/> if there is no such id.</summary>
    Task<MeterRecord?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The meter list, newest first.</summary>
    Task<IReadOnlyList<MeterRecord>> ListAsync(MeterQuery query, CancellationToken cancellationToken = default);

    /// <summary>One meter's history, oldest first, optionally narrowed to one kind of line.</summary>
    /// <exception cref="MeterNotFoundException">There is no meter with that id.</exception>
    Task<IReadOnlyList<MeterHistoryEntry>> HistoryAsync(
        Guid id,
        MeterHistoryEntryType? entryType = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The meter register over the metering schema.</summary>
/// <remarks>
/// <para>
/// Every write runs inside <see cref="IUnitOfWork.ExecuteAsync"/> and never calls
/// <c>SaveChanges</c> itself, so the meter row, its history line, its audit entry and its outbox
/// row are one transaction — invariants 1 and 2. The history line is written by the aggregate
/// rather than here, which is what makes "the meter moved but nothing recorded where" impossible.
/// </para>
/// <para>
/// The one thing this service does that no earlier registry did is read <i>another module's</i>
/// registry: fitting a meter needs to know the premise exists and is still in service, which is
/// <see cref="IServiceLocationDirectory"/>'s job. That read is a plain in-process call on the same
/// scope and therefore inside the same transaction — but it is a read, so nothing about the
/// customers schema is written from here, and nothing here would be rolled back by it.
/// </para>
/// <para>
/// <b>WP-2.17 added a second such read, and it is a refusal rather than a lookup.</b> Wastewater is
/// GridCore's first unmetered service: no device is fitted, nothing is read, and the charge is flat.
/// So a premise whose only open account takes an unmetered supply is a premise a revenue meter has
/// nothing to measure at, and <see cref="IServiceAccountDirectory"/> is what lets this module say so
/// instead of fitting one. See <see cref="AssignAsync"/> for why the rule is stated over the
/// premise's accounts rather than over one account.
/// </para>
/// </remarks>
public sealed class MeterService(
    MeteringDbContext database,
    IMeterNumberGenerator numbers,
    IServiceLocationDirectory serviceLocations,
    IServiceAccountDirectory serviceAccounts,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    IEventPublisher events,
    ICurrentUser currentUser,
    TimeProvider clock) : IMeterService
{
    /// <summary>The largest page <see cref="ListAsync"/> will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public Task<MeterRecord> RegisterAsync(RegisterMeterInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                await RequireSerialIsFreeAsync(input.SerialNumber, excluding: null, ct).ConfigureAwait(false);

                var meterNumber = await numbers.NextMeterNumberAsync(ct).ConfigureAwait(false);

                // The unique index is the real guarantee; this turns the loser of a race into a 409
                // the caller can retry rather than a 500 out of the database.
                if (await database.Meters.AnyAsync(existing => existing.MeterNumber == meterNumber, ct).ConfigureAwait(false))
                {
                    throw new MeterWorkflowException(
                        $"Meter number {meterNumber} has just been taken by another registration. Try again.");
                }

                var meter = Meter.Register(
                    meterNumber,
                    input.SerialNumber,
                    input.Type,
                    RegistryActor.Of(currentUser),
                    now,
                    input.RegisterDigits,
                    input.Manufacturer,
                    input.Model,
                    input.Note);

                database.Meters.Add(meter);

                audit.Record(
                    AuditActions.MeterRegistered,
                    AuditEntityTypes.Meter,
                    meter.Id.ToString(),
                    before: null,
                    after: MeterSnapshot.Of(meter));

                await events.PublishAsync(
                    MeterRegistered.For(
                        now,
                        meter.Id,
                        meter.MeterNumber,
                        meter.SerialNumber,
                        meter.Type.ToString(),
                        meter.Status.ToString()),
                    ct).ConfigureAwait(false);

                // Newly registered, so it is in a store and there is no premise to resolve.
                return new MeterRecord(meter, null);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<MeterRecord> UpdateAsync(Guid id, UpdateMeterInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var meter = await LoadAsync(id, ct).ConfigureAwait(false);
                var before = MeterSnapshot.Of(meter);

                await RequireSerialIsFreeAsync(input.SerialNumber, excluding: meter.Id, ct).ConfigureAwait(false);

                meter.UpdateDetails(input.SerialNumber, input.Type, input.RegisterDigits, input.Manufacturer, input.Model);

                audit.Record(AuditActions.MeterUpdated, AuditEntityTypes.Meter, meter.Id.ToString(), before, MeterSnapshot.Of(meter));

                // No event: correcting a model designation or a mistyped serial is not a fact
                // another module acts on, and publishing one would put noise in every inbox.
                return await DescribeAsync(meter, ct).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<MeterRecord> AssignAsync(Guid id, AssignMeterInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var meter = await LoadAsync(id, ct).ConfigureAwait(false);
                var before = MeterSnapshot.Of(meter);

                // The premise is another module's row, so it is checked through the directory and
                // never joined to. Both of these are 4xx before the aggregate is touched.
                var premise = await serviceLocations.FindAsync(input.ServiceLocationId, ct).ConfigureAwait(false)
                    ?? throw new ServiceLocationNotFoundException(input.ServiceLocationId);

                if (!premise.IsActive)
                {
                    // A 409 naming what is in the way: a demolished or permanently disconnected
                    // premise is not somewhere a crew fits a revenue meter.
                    throw new MeterWorkflowException(
                        $"Premise {premise.LocationCode} is not in service, so a meter cannot be fitted there.");
                }

                // WP-2.17: a meter cannot be fitted where nothing it could measure is taken.
                //
                // The rule is stated over the premise's OPEN ACCOUNTS rather than over one account,
                // because a meter is fitted to a premise and not to an account (see Meter's own
                // remarks) — the device stays on the wall when the occupant leaves. So the question
                // is not "is this account unmetered" but "is every supply taken here unmetered", and
                // only the accounts can answer it.
                //
                // No accounts at all is allowed, deliberately. A new build is metered before anybody
                // applies for supply, and the demo seeders fit meters before they open accounts;
                // refusing that would be this module inventing an ordering rule for somebody else's
                // registry. What is refused is the one case that is definitely wrong: a premise
                // where service IS being taken and none of it is measured.
                var openAccounts = await serviceAccounts.ListOpenAtLocationAsync(premise.Id, ct).ConfigureAwait(false);

                if (openAccounts.Count > 0 && !openAccounts.Any(account => account.IsMetered))
                {
                    var unmetered = string.Join(", ", openAccounts.Select(account => $"{account.AccountNumber} ({account.ServiceType})"));

                    throw new MeterWorkflowException(
                        $"Premise {premise.LocationCode} takes only unmetered service ({unmetered}), so a revenue meter "
                        + "has nothing to measure there. Open a metered service account before fitting one.");
                }

                // Checked here so the loser of a race reads as a conflict naming the meter it
                // collides with; ux_meters_service_location is what actually guarantees it.
                var occupant = await database.Meters
                    .Where(existing => existing.ServiceLocationId == premise.Id)
                    .Select(existing => existing.MeterNumber)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);

                if (occupant is not null)
                {
                    throw new MeterWorkflowException(
                        $"Premise {premise.LocationCode} is already metered by {occupant}. Remove that meter before fitting another.");
                }

                meter.InstallAt(premise.Id, RegistryActor.Of(currentUser), now, input.InstallationReading, input.Note);

                audit.Record(AuditActions.MeterInstalled, AuditEntityTypes.Meter, meter.Id.ToString(), before, MeterSnapshot.Of(meter));

                await events.PublishAsync(
                    MeterInstalled.For(now, meter.Id, meter.MeterNumber, meter.Type.ToString(), premise.Id),
                    ct).ConfigureAwait(false);

                return new MeterRecord(meter, premise);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<MeterRecord> RemoveAsync(Guid id, string? reason, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var meter = await LoadAsync(id, ct).ConfigureAwait(false);
                var before = MeterSnapshot.Of(meter);

                // Read before the removal clears it — the event and the history line have to name
                // the premise that has just been left unmetered.
                var premiseId = meter.ServiceLocationId;

                meter.Remove(RegistryActor.Of(currentUser), now, reason);

                audit.Record(AuditActions.MeterRemoved, AuditEntityTypes.Meter, meter.Id.ToString(), before, MeterSnapshot.Of(meter));

                await events.PublishAsync(
                    MeterRemoved.For(now, meter.Id, meter.MeterNumber, premiseId!.Value, reason),
                    ct).ConfigureAwait(false);

                // Off the wall and in the yard: no premise to describe.
                return new MeterRecord(meter, null);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<MeterRecord> ChangeStatusAsync(Guid id, MeterStatus status, string? reason, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var meter = await LoadAsync(id, ct).ConfigureAwait(false);
                var before = MeterSnapshot.Of(meter);

                meter.ChangeStatus(status, RegistryActor.Of(currentUser), now, reason);

                audit.Record(AuditActions.MeterStatusChanged, AuditEntityTypes.Meter, meter.Id.ToString(), before, MeterSnapshot.Of(meter));

                // No event, deliberately. The aggregate refuses any move that fits or unfits the
                // meter, so what is left — faulty, back in service, booked into stock, retired — never
                // changes what measures a premise, which is the only fact another module gates on.
                // WP-2.2 reads a faulty meter's exceptions out of this module's own register.
                return await DescribeAsync(meter, ct).ConfigureAwait(false);
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<MeterRecord?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var meter = await database.Meters
            .Include(meter => meter.History)
            .FirstOrDefaultAsync(meter => meter.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return meter is null ? null : await DescribeAsync(meter, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MeterRecord>> ListAsync(MeterQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No Include: a list row shows what a meter is and where it stands, not everywhere it has
        // been. The history is one more request away, on the meter being looked at.
        var meters = database.Meters.AsNoTracking();

        // Matched against non-nullable locals: the columns are stored by name, and EF cannot
        // translate a nullable-to-converted-value comparison.
        if (query.Type is { } type)
        {
            meters = meters.Where(meter => meter.Type == type);
        }

        if (query.Status is { } status)
        {
            meters = meters.Where(meter => meter.Status == status);
        }

        if (query.ServiceLocationId is { } premise)
        {
            meters = meters.Where(meter => meter.ServiceLocationId == premise);
        }

        if (query.Fitted is { } fitted)
        {
            // Expressed as the column rather than as a list of statuses, because the column is what
            // the aggregate keeps in step with the status — and it is the indexed one.
            meters = fitted
                ? meters.Where(meter => meter.ServiceLocationId != null)
                : meters.Where(meter => meter.ServiceLocationId == null);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Lower-cased on both sides rather than ILIKE, so the fast tier exercises the same SQL
            // shape production runs. A crew searches by whatever is legible on the meter — the
            // number on the utility's plate, or the manufacturer's serial.
            var term = query.Search.Trim().ToLowerInvariant();

            meters = meters.Where(meter =>
                meter.MeterNumber.ToLower().Contains(term)
                || meter.SerialNumber.ToLower().Contains(term));
        }

        // Ordered by key: ids are Guid v7, so the primary-key index already orders chronologically
        // on Postgres and on the fast tier's SQLite alike.
        var page = await meters
            .OrderByDescending(meter => meter.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return await DescribeAsync(page, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MeterHistoryEntry>> HistoryAsync(
        Guid id,
        MeterHistoryEntryType? entryType = null,
        CancellationToken cancellationToken = default)
    {
        if (!await database.Meters.AnyAsync(meter => meter.Id == id, cancellationToken).ConfigureAwait(false))
        {
            // Distinguished from a meter that simply has no lines, which cannot happen — every
            // meter is registered with one — but an empty list for a missing id would say it had.
            throw new MeterNotFoundException(id);
        }

        var history = database.MeterHistory
            .AsNoTracking()
            .Where(entry => entry.MeterId == id);

        if (entryType is { } kind)
        {
            history = history.Where(entry => entry.EntryType == kind);
        }

        return await history
            .OrderBy(entry => entry.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Meter> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await database.Meters
            .Include(meter => meter.History)
            .FirstOrDefaultAsync(meter => meter.Id == id, cancellationToken).ConfigureAwait(false)
        ?? throw new MeterNotFoundException(id);

    private async Task<MeterRecord> DescribeAsync(Meter meter, CancellationToken cancellationToken) =>
        new(
            meter,
            meter.ServiceLocationId is { } premise
                ? await serviceLocations.FindAsync(premise, cancellationToken).ConfigureAwait(false)
                : null);

    /// <summary>
    /// Resolves every fitted meter's premise in <b>one</b> directory call rather than one per row.
    /// A page of 200 meters that each asked separately would be 200 round trips across the module
    /// boundary for a list the register answers in one.
    /// </summary>
    private async Task<IReadOnlyList<MeterRecord>> DescribeAsync(
        IReadOnlyList<Meter> meters,
        CancellationToken cancellationToken)
    {
        var premiseIds = meters
            .Select(meter => meter.ServiceLocationId)
            .OfType<Guid>()
            .ToArray();

        if (premiseIds.Length is 0)
        {
            return meters.Select(meter => new MeterRecord(meter, null)).ToList();
        }

        var premises = await serviceLocations.FindManyAsync(premiseIds, cancellationToken).ConfigureAwait(false);

        return meters
            .Select(meter => new MeterRecord(
                meter,
                meter.ServiceLocationId is { } premise && premises.TryGetValue(premise, out var located) ? located : null))
            .ToList();
    }

    private async Task RequireSerialIsFreeAsync(string? serialNumber, Guid? excluding, CancellationToken cancellationToken)
    {
        var serial = RegistryText.Clean(serialNumber, Meter.SerialNumberLength);

        if (serial is null)
        {
            // Left to the aggregate, which refuses it as a 400. A missing serial is a malformed
            // registration, not a collision, and answering 409 here would say the opposite.
            return;
        }

        var taken = await database.Meters
            .Where(existing => existing.SerialNumber == serial)
            .Where(existing => excluding == null || existing.Id != excluding)
            .Select(existing => existing.MeterNumber)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (taken is not null)
        {
            // The unique index is what actually guarantees this; the check is here so the second
            // registration of one physical meter reads as a conflict naming the meter it collides
            // with, rather than a 500.
            throw new MeterWorkflowException($"Serial number '{serial}' is already registered as meter {taken}.");
        }
    }
}

/// <summary>
/// The before/after shape a meter is audited as. A dedicated record rather than the entity, so
/// changing the entity later cannot silently change the meaning of historic entries.
/// </summary>
/// <param name="Id">Which meter.</param>
/// <param name="MeterNumber">Its number.</param>
/// <param name="SerialNumber">The manufacturer's serial number.</param>
/// <param name="Type">How it measures the service.</param>
/// <param name="Manufacturer">Who made it.</param>
/// <param name="Model">Their model designation.</param>
/// <param name="RegisterDigits">How many whole digits its register carries.</param>
/// <param name="Status">Where it stands in its working life.</param>
/// <param name="ServiceLocationId">The premise it is fitted at, where it is fitted anywhere.</param>
/// <param name="InstalledAt">When it was last fitted.</param>
/// <param name="InstallationReading">What the dials read as it went on.</param>
/// <param name="StatusReason">Why the status last moved.</param>
public sealed record MeterSnapshot(
    Guid Id,
    string MeterNumber,
    string SerialNumber,
    MeterType Type,
    string? Manufacturer,
    string? Model,
    int RegisterDigits,
    MeterStatus Status,
    Guid? ServiceLocationId,
    DateTimeOffset? InstalledAt,
    decimal? InstallationReading,
    string? StatusReason)
{
    /// <summary>Takes a snapshot of <paramref name="meter"/> as it stands.</summary>
    public static MeterSnapshot Of(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);

        return new MeterSnapshot(
            meter.Id,
            meter.MeterNumber,
            meter.SerialNumber,
            meter.Type,
            meter.Manufacturer,
            meter.Model,
            meter.RegisterDigits,
            meter.Status,
            meter.ServiceLocationId,
            meter.InstalledAt,
            meter.InstallationReading,
            meter.StatusReason);
    }
}
