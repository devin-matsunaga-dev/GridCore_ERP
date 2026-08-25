using GridCore.Contracts.Directories;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Metering.Features.Meters;

/// <summary>Body of a request to enter a meter in the register.</summary>
/// <param name="SerialNumber">The manufacturer's serial number stamped on the meter.</param>
/// <param name="Type">How the meter measures the service.</param>
/// <param name="RegisterDigits">
/// How many whole digits its register carries. Defaults to the ordinary domestic five; a
/// three-phase or CT-metered intake carries more, and getting it wrong is what turns a rollover
/// into a bill for a hundred thousand units.
/// </param>
/// <param name="Manufacturer">Who made it.</param>
/// <param name="Model">Their model designation.</param>
/// <param name="Note">Why it is being registered, for the history.</param>
public sealed record RegisterMeterRequest(
    string SerialNumber,
    MeterType Type,
    int RegisterDigits = Meter.DefaultRegisterDigits,
    string? Manufacturer = null,
    string? Model = null,
    string? Note = null) : IMeterDetails;

/// <summary>Body of a request to correct a meter's device details.</summary>
/// <param name="SerialNumber">The manufacturer's serial number.</param>
/// <param name="Type">How the meter measures the service.</param>
/// <param name="RegisterDigits">How many whole digits its register carries.</param>
/// <param name="Manufacturer">Who made it.</param>
/// <param name="Model">Their model designation.</param>
public sealed record UpdateMeterRequest(
    string SerialNumber,
    MeterType Type,
    int RegisterDigits = Meter.DefaultRegisterDigits,
    string? Manufacturer = null,
    string? Model = null) : IMeterDetails;

/// <summary>Body of a request to fit a meter at a premise.</summary>
/// <param name="ServiceLocationId">The premise it goes to.</param>
/// <param name="InstallationReading">What the dials read as it went on.</param>
/// <param name="Note">Why, for the history and the audit trail.</param>
public sealed record AssignMeterRequest(
    Guid ServiceLocationId,
    decimal? InstallationReading = null,
    string? Note = null);

/// <summary>Body of a request to take a meter off a premise.</summary>
/// <param name="Reason">Why it came off, for the history and the audit trail.</param>
public sealed record RemoveMeterRequest(string? Reason = null);

/// <summary>Body of a request to move a meter through its lifecycle.</summary>
/// <param name="Status">Where it should end up. Fitting and unfitting are <c>assign</c> and <c>remove</c>.</param>
/// <param name="Reason">Why, for the history and the audit trail.</param>
public sealed record ChangeMeterStatusRequest(MeterStatus Status, string? Reason = null);

/// <summary>One line of a meter's history as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="EntryType">What kind of thing happened.</param>
/// <param name="FromStatus">Where the meter was. Absent on the opening line.</param>
/// <param name="ToStatus">Where it went.</param>
/// <param name="ServiceLocationId">The premise involved, on an installation or a removal line.</param>
/// <param name="Note">Why, or what was done.</param>
/// <param name="ActorId">Subject id of whoever did it.</param>
/// <param name="ActorName">Their name at the time.</param>
/// <param name="RecordedAt">When.</param>
public sealed record MeterHistoryEntryResponse(
    Guid Id,
    string EntryType,
    string? FromStatus,
    string ToStatus,
    Guid? ServiceLocationId,
    string? Note,
    string ActorId,
    string? ActorName,
    DateTimeOffset RecordedAt)
{
    /// <summary>Projects a history entry for the wire.</summary>
    public static MeterHistoryEntryResponse From(MeterHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new MeterHistoryEntryResponse(
            entry.Id,
            entry.EntryType.ToString(),
            entry.FromStatus?.ToString(),
            entry.ToStatus.ToString(),
            entry.ServiceLocationId,
            entry.Note,
            entry.ActorId,
            entry.ActorName,
            entry.RecordedAt);
    }
}

/// <summary>The premise a meter is fitted at, as the meter register reports it.</summary>
/// <remarks>
/// Resolved through <c>IServiceLocationDirectory</c>, so a screen showing the register does not
/// have to make a second round trip per row to learn what "premise 0193…" means. It is the
/// Customers module's data and this is a read-only copy of it on the wire, never a join.
/// </remarks>
/// <param name="Id">Identifier of the premise.</param>
/// <param name="LocationCode">The code quoted on a work order.</param>
/// <param name="FormattedAddress">The one-line address.</param>
/// <param name="IsActive">Whether service may still be delivered there.</param>
public sealed record MeterServiceLocationResponse(Guid Id, string LocationCode, string FormattedAddress, bool IsActive)
{
    /// <summary>Projects a premise summary for the wire.</summary>
    public static MeterServiceLocationResponse From(ServiceLocationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new MeterServiceLocationResponse(summary.Id, summary.LocationCode, summary.FormattedAddress, summary.IsActive);
    }
}

/// <summary>A meter as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="MeterNumber">The number the utility knows it by.</param>
/// <param name="SerialNumber">The manufacturer's serial number.</param>
/// <param name="Type">How it measures the service.</param>
/// <param name="Manufacturer">Who made it.</param>
/// <param name="Model">Their model designation.</param>
/// <param name="RegisterDigits">How many whole digits its register carries.</param>
/// <param name="RegisterCapacity">What that register counts up to before it returns to zero.</param>
/// <param name="Status">Where it stands in its working life.</param>
/// <param name="IsFitted">Whether it is on a premise and measuring supply.</param>
/// <param name="AllowedTransitions">Every status the machine allows from here.</param>
/// <param name="AllowedStatusChanges">
/// The subset reachable through <c>POST /status</c>. Fitting and unfitting are <c>assign</c> and
/// <c>remove</c>, so a UI renders buttons from this rather than from the full list.
/// </param>
/// <param name="ServiceLocationId">The premise it is fitted at, where it is fitted anywhere.</param>
/// <param name="ServiceLocation">That premise, resolved through the Customers module.</param>
/// <param name="InstalledAt">When it was last fitted.</param>
/// <param name="InstallationReading">What the dials read as it went on.</param>
/// <param name="RegisteredAt">When it was entered in the register.</param>
/// <param name="StatusChangedAt">When the status last moved.</param>
/// <param name="StatusReason">Why it last moved.</param>
/// <param name="History">The meter's history, oldest first. Empty on a list row.</param>
public sealed record MeterResponse(
    Guid Id,
    string MeterNumber,
    string SerialNumber,
    string Type,
    string? Manufacturer,
    string? Model,
    int RegisterDigits,
    decimal RegisterCapacity,
    string Status,
    bool IsFitted,
    IReadOnlyList<string> AllowedTransitions,
    IReadOnlyList<string> AllowedStatusChanges,
    Guid? ServiceLocationId,
    MeterServiceLocationResponse? ServiceLocation,
    DateTimeOffset? InstalledAt,
    decimal? InstallationReading,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? StatusChangedAt,
    string? StatusReason,
    IReadOnlyList<MeterHistoryEntryResponse> History)
{
    /// <summary>Projects a <see cref="MeterRecord"/> for the wire, with whatever history is loaded.</summary>
    public static MeterResponse From(MeterRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var meter = record.Meter;

        return new MeterResponse(
            meter.Id,
            meter.MeterNumber,
            meter.SerialNumber,
            meter.Type.ToString(),
            meter.Manufacturer,
            meter.Model,
            meter.RegisterDigits,
            meter.RegisterCapacity,
            meter.Status.ToString(),
            meter.IsFitted,
            meter.AllowedTransitions.Select(status => status.ToString()).ToList(),
            meter.AllowedStatusChanges.Select(status => status.ToString()).ToList(),
            meter.ServiceLocationId,
            record.ServiceLocation is null ? null : MeterServiceLocationResponse.From(record.ServiceLocation),
            meter.InstalledAt,
            meter.InstallationReading,
            meter.RegisteredAt,
            meter.StatusChangedAt,
            meter.StatusReason,
            meter.History
                .OrderBy(entry => entry.Id)
                .Select(MeterHistoryEntryResponse.From)
                .ToList());
    }
}

/// <summary>The meter register's HTTP surface.</summary>
public static class MeterEndpoints
{
    /// <summary>Route prefix of the meter register.</summary>
    public const string RoutePrefix = "/api/meters";

    /// <summary>Maps the meter endpoints.</summary>
    public static IEndpointRouteBuilder MapMeterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Metering");

        group
            .MapGet("/", async (
                    string? search,
                    MeterType? type,
                    MeterStatus? status,
                    Guid? serviceLocationId,
                    bool? fitted,
                    int? limit,
                    [FromServices] IMeterService meters,
                    CancellationToken cancellationToken) =>
                Results.Ok((await meters.ListAsync(
                        new MeterQuery(search, type, status, serviceLocationId, fitted, limit ?? 50),
                        cancellationToken))
                    .Select(MeterResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Metering.Read)
            .WithName("ListMeters");

        group
            .MapGet("/{id:guid}", async ([FromRoute] Guid id, [FromServices] IMeterService meters, CancellationToken cancellationToken) =>
            {
                var meter = await meters.FindAsync(id, cancellationToken);

                return meter is null ? MeterProblems.MeterNotFound(id) : Results.Ok(MeterResponse.From(meter));
            })
            .RequirePermission(Permissions.Metering.Read)
            .WithName("GetMeter");

        // Where this meter has been. Its own resource rather than a field of the meter, because it
        // is the answer to "what was measuring this premise in March" — a question the meter row
        // itself cannot answer once the device has moved on.
        group
            .MapGet("/{id:guid}/history", (
                    [FromRoute] Guid id,
                    MeterHistoryEntryType? entryType,
                    [FromServices] IMeterService meters,
                    CancellationToken cancellationToken) =>
                MeterProblems.RunAsync(async () =>
                    Results.Ok((await meters.HistoryAsync(id, entryType, cancellationToken))
                        .Select(MeterHistoryEntryResponse.From)
                        .ToList())))
            .RequirePermission(Permissions.Metering.Read)
            .WithName("GetMeterHistory");

        group
            .MapPost("/", (RegisterMeterRequest body, [FromServices] IMeterService meters, CancellationToken cancellationToken) =>
                MeterProblems.RunAsync(async () =>
                {
                    var meter = await meters.RegisterAsync(
                        new RegisterMeterInput(body.SerialNumber, body.Type, body.RegisterDigits, body.Manufacturer, body.Model, body.Note),
                        cancellationToken);

                    return Results.Created($"{RoutePrefix}/{meter.Meter.Id}", MeterResponse.From(meter));
                }))
            .RequirePermission(Permissions.Metering.Write)
            .WithValidation<RegisterMeterRequest>()
            .WithName("RegisterMeter");

        group
            .MapPut("/{id:guid}", ([FromRoute] Guid id, UpdateMeterRequest body, [FromServices] IMeterService meters, CancellationToken cancellationToken) =>
                MeterProblems.RunAsync(async () =>
                    Results.Ok(MeterResponse.From(await meters.UpdateAsync(
                        id,
                        new UpdateMeterInput(body.SerialNumber, body.Type, body.RegisterDigits, body.Manufacturer, body.Model),
                        cancellationToken)))))
            .RequirePermission(Permissions.Metering.Write)
            .WithValidation<UpdateMeterRequest>()
            .WithName("UpdateMeter");

        // Assignment is the work package's headline verb and a POST sub-resource per CONVENTIONS.md,
        // never a PUT of a service_location_id field: fitting a meter is an act with a date, a
        // reading and a reason, and it is refused with a 409 when the premise already has one.
        group
            .MapPost("/{id:guid}/assign", ([FromRoute] Guid id, AssignMeterRequest body, [FromServices] IMeterService meters, CancellationToken cancellationToken) =>
                MeterProblems.RunAsync(async () =>
                    Results.Ok(MeterResponse.From(await meters.AssignAsync(
                        id,
                        new AssignMeterInput(body.ServiceLocationId, body.InstallationReading, body.Note),
                        cancellationToken)))))
            .RequirePermission(Permissions.Metering.Write)
            .WithValidation<AssignMeterRequest>()
            .WithName("AssignMeter");

        group
            .MapPost("/{id:guid}/remove", ([FromRoute] Guid id, RemoveMeterRequest body, [FromServices] IMeterService meters, CancellationToken cancellationToken) =>
                MeterProblems.RunAsync(async () =>
                    Results.Ok(MeterResponse.From(await meters.RemoveAsync(id, body.Reason, cancellationToken)))))
            .RequirePermission(Permissions.Metering.Write)
            .WithValidation<RemoveMeterRequest>()
            .WithName("RemoveMeter");

        // The rest of the lifecycle: faulty, back in service, booked into stock, retired. The
        // aggregate refuses anything here that would fit or unfit the meter, naming the endpoint
        // that does.
        group
            .MapPost("/{id:guid}/status", ([FromRoute] Guid id, ChangeMeterStatusRequest body, [FromServices] IMeterService meters, CancellationToken cancellationToken) =>
                MeterProblems.RunAsync(async () =>
                    Results.Ok(MeterResponse.From(await meters.ChangeStatusAsync(id, body.Status, body.Reason, cancellationToken)))))
            .RequirePermission(Permissions.Metering.Write)
            .WithValidation<ChangeMeterStatusRequest>()
            .WithName("ChangeMeterStatus");

        return endpoints;
    }
}
