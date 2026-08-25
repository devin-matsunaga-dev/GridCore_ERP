using GridCore.Contracts.Events;
using GridCore.Contracts.Providers;
using GridCore.Modules.Metering.Data;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Metering.Features.Readings;

/// <summary>What a caller supplies to record one reading by hand.</summary>
/// <param name="Reading">
/// What the dials read, or <see langword="null"/> to record that the meter could not be read.
/// </param>
/// <param name="ReadingDate">When the dials were read. Defaults to now.</param>
/// <param name="Note">What the reader wants recorded against it.</param>
public sealed record RecordReadingInput(decimal? Reading, DateTimeOffset? ReadingDate = null, string? Note = null);

/// <summary>What a caller supplies to run a reading cycle through the provider.</summary>
/// <param name="CycleCode">What the utility calls this run, e.g. <c>2026-08</c>. Unique per meter.</param>
/// <param name="ReadAt">The date the meters are read as at. Defaults to now.</param>
/// <param name="Seed">
/// Seed for the provider's own randomness. The same cycle re-run with the same seed produces the
/// same readings, which is what makes a demonstration reconcilable.
/// </param>
public sealed record RunReadingCycleInput(string CycleCode, DateTimeOffset? ReadAt = null, int Seed = 0);

/// <summary>How the reading register is filtered.</summary>
/// <param name="MeterId">Only readings off this meter.</param>
/// <param name="ServiceLocationId">Only readings at this premise, across every meter that has stood there.</param>
/// <param name="ExceptionCode">Only readings carrying this exception code.</param>
/// <param name="ExceptionsOnly">
/// <see langword="true"/> for everything on the worklist, without naming three codes. The question
/// "what came back from that cycle that somebody has to look at".
/// </param>
/// <param name="CycleCode">Only readings from this cycle.</param>
/// <param name="Limit">Most rows to return.</param>
public sealed record MeterReadingQuery(
    Guid? MeterId = null,
    Guid? ServiceLocationId = null,
    ReadingExceptionCode? ExceptionCode = null,
    bool? ExceptionsOnly = null,
    string? CycleCode = null,
    int Limit = 50);

/// <summary>What a reading cycle produced.</summary>
/// <param name="CycleCode">The cycle that was read.</param>
/// <param name="ReadAt">The date it was read as at.</param>
/// <param name="Seed">The seed the batch came from — quote it to reproduce this run exactly.</param>
/// <param name="Provider">Which provider produced it, for the audit trail.</param>
/// <param name="Readings">Every reading recorded, in the order the meters were read.</param>
public sealed record ReadingCycleResult(
    string CycleCode,
    DateTimeOffset ReadAt,
    int Seed,
    string Provider,
    IReadOnlyList<MeterReading> Readings)
{
    /// <summary>How many readings were recorded.</summary>
    public int Recorded => Readings.Count;

    /// <summary>How many of them are on the exception worklist.</summary>
    public int Exceptions => Readings.Count(reading => reading.IsException);

    /// <summary>How many carry each exception code, for the audit entry and the response.</summary>
    public IReadOnlyDictionary<string, int> ByExceptionCode =>
        Readings
            .GroupBy(reading => reading.ExceptionCode.ToString())
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}

/// <summary>The reading register. Endpoints are a thin layer over it.</summary>
public interface IMeterReadingService
{
    /// <summary>Records one reading against a fitted meter.</summary>
    /// <exception cref="MeterNotFoundException">There is no meter with that id.</exception>
    /// <exception cref="MeterWorkflowException">The meter is not fitted, or the reading is out of order.</exception>
    /// <exception cref="MeterValidationException">The reading is not one that meter could have produced.</exception>
    Task<MeterReading> RecordAsync(Guid meterId, RecordReadingInput input, CancellationToken cancellationToken = default);

    /// <summary>Reads every fitted meter through the provider and records the batch.</summary>
    /// <exception cref="MeterWorkflowException">This cycle has already been read.</exception>
    Task<ReadingCycleResult> RunCycleAsync(RunReadingCycleInput input, CancellationToken cancellationToken = default);

    /// <summary>The reading register, newest first.</summary>
    Task<IReadOnlyList<MeterReading>> ListAsync(MeterReadingQuery query, CancellationToken cancellationToken = default);

    /// <summary>One meter's readings, newest first.</summary>
    /// <exception cref="MeterNotFoundException">There is no meter with that id.</exception>
    Task<IReadOnlyList<MeterReading>> ForMeterAsync(Guid meterId, int limit, CancellationToken cancellationToken = default);
}

/// <summary>The reading register over the metering schema.</summary>
/// <remarks>
/// <para>
/// Every write runs inside <see cref="IUnitOfWork.ExecuteAsync"/> and never calls
/// <c>SaveChanges</c> itself, so the reading, its audit entry and its outbox row are one
/// transaction — invariants 1 and 2.
/// </para>
/// <para>
/// The arithmetic is not here. Consumption, rollover and the exception code are
/// <see cref="MeterReading.Record"/>'s work, decided from a <see cref="ReadingBaseline"/> this
/// service assembles out of the database. That split is deliberate and is what CONVENTIONS.md's ⚡
/// rules ask for: the part that must be tested exhaustively is pure, and the part that needs a row
/// is the thin part.
/// </para>
/// </remarks>
public sealed class MeterReadingService(
    MeteringDbContext database,
    IMeterReadingProvider provider,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    IEventPublisher events,
    ICurrentUser currentUser,
    TimeProvider clock) : IMeterReadingService
{
    /// <summary>The largest page a list will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// How many recent readings at a premise the high-usage baseline is averaged over. Six is about
    /// half a year of monthly cycles: long enough to survive one odd month, short enough that a
    /// household that has genuinely changed its usage stops being flagged for it.
    /// </summary>
    public const int BaselineWindow = 6;

    /// <summary>
    /// Most meters one cycle will read. A cap rather than paging: a reading run is one transaction,
    /// and a route that outgrows this wants splitting into rounds rather than a longer transaction.
    /// </summary>
    public const int MaxCycleSize = 500;

    /// <inheritdoc />
    public Task<MeterReading> RecordAsync(Guid meterId, RecordReadingInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var meter = await database.Meters.FirstOrDefaultAsync(candidate => candidate.Id == meterId, ct).ConfigureAwait(false)
                    ?? throw new MeterNotFoundException(meterId);

                var reading = MeterReading.Record(
                    meter,
                    await BaselineAsync(meter, ct).ConfigureAwait(false),
                    input.Reading,
                    input.ReadingDate ?? now,
                    MeterReadingSource.Manual,
                    RegistryActor.Of(currentUser),
                    now,
                    cycleCode: null,
                    input.Note);

                database.MeterReadings.Add(reading);

                audit.Record(
                    AuditActions.MeterReadingRecorded,
                    AuditEntityTypes.MeterReading,
                    reading.Id.ToString(),
                    before: null,
                    after: MeterReadingSnapshot.Of(reading, meter.MeterNumber));

                await PublishAsync(reading, meter.MeterNumber, now, ct).ConfigureAwait(false);

                return reading;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ReadingCycleResult> RunCycleAsync(RunReadingCycleInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();
                var readAt = input.ReadAt ?? now;

                var cycleCode = RegistryText.Clean(input.CycleCode, MeterReading.CycleCodeLength)
                    ?? throw new MeterValidationException("A reading cycle must be given a cycle code.");

                // Checked here so a second press of the button reads as a conflict naming the cycle;
                // ux_meter_readings_meter_cycle is what actually guarantees it.
                if (await database.MeterReadings.AnyAsync(reading => reading.CycleCode == cycleCode, ct).ConfigureAwait(false))
                {
                    throw new MeterWorkflowException(
                        $"Reading cycle '{cycleCode}' has already been read. A correction is a new reading, not a re-run.");
                }

                // Only fitted meters are on a route: a meter in a store measures nothing, and the
                // column is the indexed one the aggregate keeps in step with the status.
                var meters = await database.Meters
                    .Where(meter => meter.ServiceLocationId != null)
                    .OrderBy(meter => meter.Id)
                    .Take(MaxCycleSize)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                var batch = await provider.ReadCycleAsync(
                        new MeterReadingCycle(cycleCode, readAt, input.Seed, await RouteAsync(meters, ct).ConfigureAwait(false)),
                        ct)
                    .ConfigureAwait(false);

                var byId = meters.ToDictionary(meter => meter.Id);
                var recorded = new List<MeterReading>(batch.Readings.Count);

                foreach (var result in batch.Readings)
                {
                    // A provider that answers for a meter nobody asked about is ignored rather than
                    // trusted: it is not on this route, and recording it would attach a reading to a
                    // meter whose baseline was never assembled.
                    if (!byId.TryGetValue(result.MeterId, out var meter))
                    {
                        continue;
                    }

                    var reading = MeterReading.Record(
                        meter,
                        await BaselineAsync(meter, ct).ConfigureAwait(false),
                        result.Reading,
                        result.ReadAt,
                        MeterReadingSource.Cycle,
                        RegistryActor.Of(currentUser),
                        now,
                        cycleCode,
                        result.Note);

                    database.MeterReadings.Add(reading);
                    recorded.Add(reading);

                    await PublishAsync(reading, meter.MeterNumber, now, ct).ConfigureAwait(false);
                }

                var cycle = new ReadingCycleResult(cycleCode, readAt, input.Seed, provider.Name, recorded);

                // ONE audit entry for the run, not one per reading. Invariant 1 is about the write
                // endpoint, and a cycle is one act: what an auditor asks is "who ran the August
                // cycle and what came back", which this answers in a line. Each reading is already
                // an immutable row stamped with who recorded it, so nothing is lost.
                audit.Record(
                    AuditActions.MeterReadingCycleRun,
                    AuditEntityTypes.MeterReadingCycle,
                    cycleCode,
                    before: null,
                    after: ReadingCycleSnapshot.Of(cycle));

                return cycle;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MeterReading>> ListAsync(MeterReadingQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var readings = database.MeterReadings.AsNoTracking();

        // Matched against non-nullable locals: the columns are stored by name, and EF cannot
        // translate a nullable-to-converted-value comparison.
        if (query.MeterId is { } meter)
        {
            readings = readings.Where(reading => reading.MeterId == meter);
        }

        if (query.ServiceLocationId is { } premise)
        {
            readings = readings.Where(reading => reading.ServiceLocationId == premise);
        }

        if (query.ExceptionCode is { } code)
        {
            readings = readings.Where(reading => reading.ExceptionCode == code);
        }

        if (query.ExceptionsOnly is true)
        {
            // Expressed as "not None" rather than a list of three codes, so a code added later
            // joins the worklist without this line being remembered.
            readings = readings.Where(reading => reading.ExceptionCode != ReadingExceptionCode.None);
        }

        if (!string.IsNullOrWhiteSpace(query.CycleCode))
        {
            var cycle = query.CycleCode.Trim();

            readings = readings.Where(reading => reading.CycleCode == cycle);
        }

        // Ordered by key: ids are Guid v7, so the primary-key index already orders chronologically
        // on Postgres and on the fast tier's SQLite alike.
        return await readings
            .OrderByDescending(reading => reading.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MeterReading>> ForMeterAsync(Guid meterId, int limit, CancellationToken cancellationToken = default)
    {
        if (!await database.Meters.AnyAsync(meter => meter.Id == meterId, cancellationToken).ConfigureAwait(false))
        {
            // Distinguished from a meter that has simply never been read, which is an empty list and
            // a different answer.
            throw new MeterNotFoundException(meterId);
        }

        return await ListAsync(new MeterReadingQuery(MeterId: meterId, Limit: limit), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Describes the meters on a route for the provider, in the terms Contracts speaks.</summary>
    private async Task<IReadOnlyList<MeterReadingRequest>> RouteAsync(
        IReadOnlyList<Meter> meters,
        CancellationToken cancellationToken)
    {
        var route = new List<MeterReadingRequest>(meters.Count);

        foreach (var meter in meters)
        {
            var baseline = await BaselineAsync(meter, cancellationToken).ConfigureAwait(false);

            route.Add(new MeterReadingRequest(
                meter.Id,
                meter.MeterNumber,
                // By name, never the enum: Contracts takes no dependency on this module's types.
                meter.Type.ToString(),
                meter.RegisterDigits,
                baseline.Reading,
                baseline.ReadAt));
        }

        return route;
    }

    /// <summary>
    /// Assembles what a new reading is measured against: the last dials this meter showed on its
    /// current fitting, and what the premise normally uses in a day.
    /// </summary>
    /// <remarks>
    /// Two queries per meter rather than one, and deliberately so. The previous <b>dial</b> reading
    /// has to be exact — it is one side of every consumption figure — so it is fetched as itself
    /// rather than hoped for inside a window; six consecutive missing reads would otherwise push it
    /// out and quietly measure a period from the installation reading instead. The usage profile is
    /// an average and a window is all it needs. Both are single index lookups, and a route is capped
    /// at <see cref="MaxCycleSize"/>; if a real one ever outgrows that, the fix is a windowed query,
    /// not a longer transaction.
    /// </remarks>
    private async Task<ReadingBaseline> BaselineAsync(Meter meter, CancellationToken cancellationToken)
    {
        if (meter.ServiceLocationId is not { } premise)
        {
            return ReadingBaseline.None;
        }

        var previous = await database.MeterReadings.AsNoTracking()
            .Where(reading => reading.MeterId == meter.Id && reading.Reading != null)
            .OrderByDescending(reading => reading.Id)
            .Take(1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var atPremise = await database.MeterReadings.AsNoTracking()
            .Where(reading => reading.ServiceLocationId == premise)
            .OrderByDescending(reading => reading.Id)
            .Take(BaselineWindow)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The exact previous first, so ReadingBaseline.From finds it whether or not the premise
        // window happened to reach that far back. The rules about which of these count — the
        // fitting boundary, missing reads, other meters at the premise — are all in there, where
        // they can be tested without a database.
        return ReadingBaseline.From(meter, [.. previous, .. atPremise]);
    }

    private Task PublishAsync(MeterReading reading, string meterNumber, DateTimeOffset now, CancellationToken cancellationToken) =>
        events.PublishAsync(
            MeterReadingRecorded.For(
                now,
                reading.Id,
                reading.MeterId,
                meterNumber,
                reading.ServiceLocationId,
                reading.ReadingDate,
                reading.Reading,
                reading.Consumption,
                reading.ExceptionCode.ToString(),
                reading.CycleCode),
            cancellationToken);
}

/// <summary>
/// The shape a reading is audited as. A dedicated record rather than the entity, so changing the
/// entity later cannot silently change the meaning of historic entries.
/// </summary>
/// <param name="Id">Which reading.</param>
/// <param name="MeterId">The meter it came off.</param>
/// <param name="MeterNumber">Its number, so the entry is readable without a second lookup.</param>
/// <param name="ServiceLocationId">The premise it was measuring.</param>
/// <param name="ReadingDate">When the dials were read.</param>
/// <param name="Reading">What they read.</param>
/// <param name="PreviousReading">What they last read.</param>
/// <param name="Consumption">Units between the two.</param>
/// <param name="RolledOver">Whether the register wrapped.</param>
/// <param name="ExceptionCode">Why it is on the worklist, if it is.</param>
/// <param name="Source">Where the reading came from.</param>
/// <param name="CycleCode">The cycle it belongs to.</param>
public sealed record MeterReadingSnapshot(
    Guid Id,
    Guid MeterId,
    string MeterNumber,
    Guid ServiceLocationId,
    DateTimeOffset ReadingDate,
    decimal? Reading,
    decimal? PreviousReading,
    decimal? Consumption,
    bool RolledOver,
    ReadingExceptionCode ExceptionCode,
    MeterReadingSource Source,
    string? CycleCode)
{
    /// <summary>Takes a snapshot of <paramref name="reading"/> as it was recorded.</summary>
    public static MeterReadingSnapshot Of(MeterReading reading, string meterNumber)
    {
        ArgumentNullException.ThrowIfNull(reading);

        return new MeterReadingSnapshot(
            reading.Id,
            reading.MeterId,
            meterNumber,
            reading.ServiceLocationId,
            reading.ReadingDate,
            reading.Reading,
            reading.PreviousReading,
            reading.Consumption,
            reading.RolledOver,
            reading.ExceptionCode,
            reading.Source,
            reading.CycleCode);
    }
}

/// <summary>The shape a reading cycle is audited as: what was run, by what, and what came back.</summary>
/// <param name="CycleCode">The cycle read.</param>
/// <param name="ReadAt">The date it was read as at.</param>
/// <param name="Seed">The seed — quoted so the run can be reproduced exactly.</param>
/// <param name="Provider">Which provider produced the batch.</param>
/// <param name="Recorded">How many readings were recorded.</param>
/// <param name="Exceptions">How many are on the worklist.</param>
/// <param name="ByExceptionCode">How many carry each code.</param>
public sealed record ReadingCycleSnapshot(
    string CycleCode,
    DateTimeOffset ReadAt,
    int Seed,
    string Provider,
    int Recorded,
    int Exceptions,
    IReadOnlyDictionary<string, int> ByExceptionCode)
{
    /// <summary>Takes a snapshot of what <paramref name="cycle"/> produced.</summary>
    public static ReadingCycleSnapshot Of(ReadingCycleResult cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        return new ReadingCycleSnapshot(
            cycle.CycleCode,
            cycle.ReadAt,
            cycle.Seed,
            cycle.Provider,
            cycle.Recorded,
            cycle.Exceptions,
            cycle.ByExceptionCode);
    }
}
