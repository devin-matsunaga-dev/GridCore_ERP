using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Metering.Features.Readings;

/// <summary>Body of a request to record one reading by hand.</summary>
/// <param name="Reading">
/// What the dials read, or <see langword="null"/> to record that the meter could not be read at all.
/// </param>
/// <param name="ReadingDate">When the dials were read. Defaults to now.</param>
/// <param name="Note">What the reader wants recorded against it.</param>
public sealed record RecordMeterReadingRequest(decimal? Reading, DateTimeOffset? ReadingDate = null, string? Note = null);

/// <summary>Body of a request to run a reading cycle.</summary>
/// <param name="CycleCode">What the utility calls this run, e.g. <c>2026-08</c>.</param>
/// <param name="ReadAt">The date the meters are read as at. Defaults to now.</param>
/// <param name="Seed">Seed for the provider's randomness. The same seed reproduces the same batch.</param>
public sealed record RunReadingCycleRequest(string CycleCode, DateTimeOffset? ReadAt = null, int Seed = 0);

/// <summary>A reading as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="MeterId">The meter it came off.</param>
/// <param name="ServiceLocationId">The premise it was measuring.</param>
/// <param name="ReadingDate">When the dials were read.</param>
/// <param name="Reading">What they read; absent for a missing read.</param>
/// <param name="Source">Where the reading came from.</param>
/// <param name="PreviousReading">What this meter last read here.</param>
/// <param name="PreviousReadingDate">When that was.</param>
/// <param name="Consumption">Units used since then.</param>
/// <param name="Days">Days the period covers.</param>
/// <param name="DailyConsumption">Units per day — the comparable figure across unequal periods.</param>
/// <param name="RolledOver">Whether the register wrapped during the period.</param>
/// <param name="ExceptionCode">Why it is on the worklist, or <c>None</c>.</param>
/// <param name="IsException">Whether it is on the worklist at all.</param>
/// <param name="CycleCode">The reading cycle it belongs to, for a cycle read.</param>
/// <param name="Note">What the reader recorded against it.</param>
/// <param name="ActorId">Subject id of whoever recorded it.</param>
/// <param name="ActorName">Their name at the time.</param>
/// <param name="RecordedAt">When it was entered in the register.</param>
public sealed record MeterReadingResponse(
    Guid Id,
    Guid MeterId,
    Guid ServiceLocationId,
    DateTimeOffset ReadingDate,
    decimal? Reading,
    string Source,
    decimal? PreviousReading,
    DateTimeOffset? PreviousReadingDate,
    decimal? Consumption,
    int? Days,
    decimal? DailyConsumption,
    bool RolledOver,
    string ExceptionCode,
    bool IsException,
    string? CycleCode,
    string? Note,
    string ActorId,
    string? ActorName,
    DateTimeOffset RecordedAt)
{
    /// <summary>Projects a reading for the wire.</summary>
    public static MeterReadingResponse From(MeterReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        return new MeterReadingResponse(
            reading.Id,
            reading.MeterId,
            reading.ServiceLocationId,
            reading.ReadingDate,
            reading.Reading,
            reading.Source.ToString(),
            reading.PreviousReading,
            reading.PreviousReadingDate,
            reading.Consumption,
            reading.Days,
            reading.DailyConsumption,
            reading.RolledOver,
            reading.ExceptionCode.ToString(),
            reading.IsException,
            reading.CycleCode,
            reading.Note,
            reading.ActorId,
            reading.ActorName,
            reading.RecordedAt);
    }
}

/// <summary>What a reading cycle produced, as the API returns it.</summary>
/// <param name="CycleCode">The cycle that was read.</param>
/// <param name="ReadAt">The date it was read as at.</param>
/// <param name="Seed">The seed — quote it to reproduce this run exactly.</param>
/// <param name="Provider">Which provider produced the batch.</param>
/// <param name="Recorded">How many readings were recorded.</param>
/// <param name="Exceptions">How many are on the worklist.</param>
/// <param name="ByExceptionCode">How many carry each exception code.</param>
/// <param name="Readings">Every reading recorded.</param>
public sealed record ReadingCycleResponse(
    string CycleCode,
    DateTimeOffset ReadAt,
    int Seed,
    string Provider,
    int Recorded,
    int Exceptions,
    IReadOnlyDictionary<string, int> ByExceptionCode,
    IReadOnlyList<MeterReadingResponse> Readings)
{
    /// <summary>Projects a cycle result for the wire.</summary>
    public static ReadingCycleResponse From(ReadingCycleResult cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        return new ReadingCycleResponse(
            cycle.CycleCode,
            cycle.ReadAt,
            cycle.Seed,
            cycle.Provider,
            cycle.Recorded,
            cycle.Exceptions,
            cycle.ByExceptionCode,
            cycle.Readings.Select(MeterReadingResponse.From).ToList());
    }
}

/// <summary>The reading register's HTTP surface.</summary>
public static class MeterReadingEndpoints
{
    /// <summary>Route prefix of the reading register.</summary>
    public const string RoutePrefix = "/api/meter-readings";

    /// <summary>Default page size for a reading list.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Maps the reading endpoints.</summary>
    public static IEndpointRouteBuilder MapMeterReadingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var readings = endpoints.MapGroup(RoutePrefix).WithTags("Metering");

        // The register-wide list, and with ?exceptionsOnly=true the worklist a billing officer works
        // before a cycle is billed. Filtered on the server; there is no sort and no total, the same
        // shape every GridCore registry list has (WP-1.5).
        readings
            .MapGet("/", async (
                    Guid? meterId,
                    Guid? serviceLocationId,
                    ReadingExceptionCode? exceptionCode,
                    bool? exceptionsOnly,
                    string? cycleCode,
                    int? limit,
                    [FromServices] IMeterReadingService register,
                    CancellationToken cancellationToken) =>
                Results.Ok((await register.ListAsync(
                        new MeterReadingQuery(meterId, serviceLocationId, exceptionCode, exceptionsOnly, cycleCode, limit ?? DefaultPageSize),
                        cancellationToken))
                    .Select(MeterReadingResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Metering.Read)
            .WithName("ListMeterReadings");

        // Running a cycle is a POST sub-resource, never a GET: it writes a batch of readings, an
        // audit entry and an outbox row per reading.
        readings
            .MapPost("/cycles", (
                    RunReadingCycleRequest body,
                    [FromServices] IMeterReadingService register,
                    CancellationToken cancellationToken) =>
                MeterProblems.RunAsync(async () =>
                    Results.Ok(ReadingCycleResponse.From(await register.RunCycleAsync(
                        new RunReadingCycleInput(body.CycleCode, body.ReadAt, body.Seed),
                        cancellationToken)))))
            .RequirePermission(Permissions.Metering.Write)
            .WithValidation<RunReadingCycleRequest>()
            .WithName("RunReadingCycle");

        // A meter's own readings hang off the meter, where somebody looking at a device expects
        // them — the same call WP-2.1 made for its history.
        var meters = endpoints.MapGroup(MeterEndpoints.RoutePrefix).WithTags("Metering");

        meters
            .MapGet("/{id:guid}/readings", (
                    [FromRoute] Guid id,
                    int? limit,
                    [FromServices] IMeterReadingService register,
                    CancellationToken cancellationToken) =>
                MeterProblems.RunAsync(async () =>
                    Results.Ok((await register.ForMeterAsync(id, limit ?? DefaultPageSize, cancellationToken))
                        .Select(MeterReadingResponse.From)
                        .ToList())))
            .RequirePermission(Permissions.Metering.Read)
            .WithName("GetMeterReadings");

        meters
            .MapPost("/{id:guid}/readings", (
                    [FromRoute] Guid id,
                    RecordMeterReadingRequest body,
                    [FromServices] IMeterReadingService register,
                    CancellationToken cancellationToken) =>
                MeterProblems.RunAsync(async () =>
                {
                    var reading = await register.RecordAsync(
                        id,
                        new RecordReadingInput(body.Reading, body.ReadingDate, body.Note),
                        cancellationToken);

                    return Results.Created($"{RoutePrefix}?meterId={id}", MeterReadingResponse.From(reading));
                }))
            .RequirePermission(Permissions.Metering.Write)
            .WithValidation<RecordMeterReadingRequest>()
            .WithName("RecordMeterReading");

        return endpoints;
    }
}
