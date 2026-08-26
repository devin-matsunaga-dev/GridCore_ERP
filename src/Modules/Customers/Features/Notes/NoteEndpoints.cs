using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.Notes;

/// <summary>Body of a request to file a note against a bill, a payment or a work order.</summary>
/// <param name="Kind">Which register the row is in.</param>
/// <param name="EntityId">The row.</param>
public sealed record NoteLinkRequest(CustomerNoteLinkKind Kind, Guid EntityId);

/// <summary>Body of a request to log a note or an interaction.</summary>
/// <param name="Kind">A written note, or the contact that took place.</param>
/// <param name="Body">What was said or written.</param>
/// <param name="ServiceAccountId">The account it is about, where it is about one rather than the customer.</param>
/// <param name="FollowUpOn">The day somebody has to come back to it.</param>
/// <param name="Link">What it is filed against.</param>
public sealed record LogNoteRequest(
    CustomerNoteKind Kind,
    string Body,
    Guid? ServiceAccountId = null,
    DateOnly? FollowUpOn = null,
    NoteLinkRequest? Link = null);

/// <summary>Body of a request to correct an earlier note by writing a new one.</summary>
/// <param name="Kind">What the contact actually was.</param>
/// <param name="Body">What it should have said.</param>
/// <param name="FollowUpOn">The day somebody has to come back to it.</param>
/// <param name="Link">What it is filed against.</param>
public sealed record CorrectNoteRequest(
    CustomerNoteKind Kind,
    string Body,
    DateOnly? FollowUpOn = null,
    NoteLinkRequest? Link = null);

/// <summary>Body of a request to pin a note or take it back down.</summary>
/// <param name="IsPinned">Where it should end up.</param>
public sealed record PinNoteRequest(bool IsPinned);

/// <summary>One entry in a customer's note log, as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="CustomerId">The customer it is about.</param>
/// <param name="ServiceAccountId">The account it is about, where it is about one.</param>
/// <param name="Kind">A written note, or the contact that took place.</param>
/// <param name="IsInteraction">Whether it records a contact rather than something written down.</param>
/// <param name="Body">What was said or written.</param>
/// <param name="FollowUpOn">The day somebody has to come back to it.</param>
/// <param name="LinkKind">Which register it is filed against, if any.</param>
/// <param name="LinkedEntityId">The row in that register.</param>
/// <param name="LinkedReference">
/// Its number, as printed. <see langword="null"/> on a work-order link, which is stored unverified
/// until WP-3.1 — there is no register to ask what the number is.
/// </param>
/// <param name="CorrectsNoteId">The note this one corrects, if it is a correction.</param>
/// <param name="IsPinned">Whether it sits at the top of the log.</param>
/// <param name="ActorId">Subject id of the rep who logged it.</param>
/// <param name="ActorName">Their display name at the time.</param>
/// <param name="RecordedAt">When it was logged.</param>
public sealed record CustomerNoteResponse(
    Guid Id,
    Guid CustomerId,
    Guid? ServiceAccountId,
    string Kind,
    bool IsInteraction,
    string Body,
    DateOnly? FollowUpOn,
    string? LinkKind,
    Guid? LinkedEntityId,
    string? LinkedReference,
    Guid? CorrectsNoteId,
    bool IsPinned,
    string ActorId,
    string? ActorName,
    DateTimeOffset RecordedAt)
{
    /// <summary>Projects a <see cref="CustomerNote"/> for the wire.</summary>
    public static CustomerNoteResponse From(CustomerNote note)
    {
        ArgumentNullException.ThrowIfNull(note);

        return new CustomerNoteResponse(
            note.Id,
            note.CustomerId,
            note.ServiceAccountId,
            note.Kind.ToString(),
            note.IsInteraction,
            note.Body,
            note.FollowUpOn,
            note.LinkKind?.ToString(),
            note.LinkedEntityId,
            note.LinkedReference,
            note.CorrectsNoteId,
            note.IsPinned,
            note.ActorId,
            note.ActorName,
            note.RecordedAt);
    }
}

/// <summary>
/// The note log's HTTP surface.
/// </summary>
/// <remarks>
/// <para>
/// Two prefixes, the shape <c>ContactEndpoints</c> established: a customer's log hangs off the
/// customer, and one note — already identified — is addressed on its own. The alternative makes a
/// client quote an id it already holds twice over and makes a mismatch between the two a case
/// somebody has to handle.
/// </para>
/// <para>
/// <b>The <c>PUT</c> on a note exists and always answers 409.</b> That is not an oversight dressed
/// up: a route that is simply absent answers 405 <i>Method Not Allowed</i>, which tells a client the
/// verb is unsupported here — where the truth is that this register is append-only and
/// <c>/corrections</c> is what they are looking for. WORK_PACKAGES.md asks for exactly this ("edit
/// attempt → 409"), and a refusal that says why is worth more than a silence.
/// </para>
/// <para>
/// <b>Everything is gated on the ordinary customer permissions</b> —
/// <see cref="Permissions.Customers.Read"/> to read, <see cref="Permissions.Customers.Write"/> to
/// write. Logging a call is clerical work; it does not earn a permission of its own the way taking a
/// deposit (WP-2.12) or authorising disclosure (WP-2.11) does, and inventing one would mean a rep
/// who may open an account cannot record that they spoke to somebody about it.
/// </para>
/// </remarks>
public static class NoteEndpoints
{
    /// <summary>Route of a customer's note log.</summary>
    public const string CustomerNotesRoute = "/api/customers/{customerId:guid}/notes";

    /// <summary>Route prefix of one note.</summary>
    public const string RoutePrefix = "/api/customer-notes";

    /// <summary>Maps the note endpoints.</summary>
    public static IEndpointRouteBuilder MapNoteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var customerScoped = endpoints.MapGroup(CustomerNotesRoute).WithTags("Customers");

        customerScoped
            .MapGet("/", (
                [FromRoute] Guid customerId,
                [FromQuery] CustomerNoteKind? kind,
                [FromQuery] Guid? serviceAccountId,
                [FromQuery] bool? pinnedOnly,
                [FromQuery] int? limit,
                [FromServices] ICustomerNoteService notes,
                CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var log = await notes.ListAsync(
                        customerId,
                        new CustomerNoteFilter(
                            kind,
                            serviceAccountId,
                            pinnedOnly ?? false,
                            limit ?? CustomerNoteService.DefaultLimit),
                        cancellationToken);

                    return Results.Ok(log.Select(CustomerNoteResponse.From).ToList());
                }))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("ListCustomerNotes");

        customerScoped
            .MapPost("/", (
                [FromRoute] Guid customerId,
                LogNoteRequest body,
                [FromServices] ICustomerNoteService notes,
                CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var note = await notes.LogAsync(
                        customerId,
                        new LogCustomerNoteInput(
                            body.Kind,
                            body.Body,
                            body.ServiceAccountId,
                            body.FollowUpOn,
                            body.Link is null ? null : new CustomerNoteLinkInput(body.Link.Kind, body.Link.EntityId)),
                        cancellationToken);

                    return Results.Created($"{RoutePrefix}/{note.Id}", CustomerNoteResponse.From(note));
                }))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<LogNoteRequest>()
            .WithName("LogCustomerNote");

        var noteScoped = endpoints.MapGroup(RoutePrefix).WithTags("Customers");

        noteScoped
            .MapGet("/{noteId:guid}", async (
                [FromRoute] Guid noteId,
                [FromServices] ICustomerNoteService notes,
                CancellationToken cancellationToken) =>
            {
                var note = await notes.FindAsync(noteId, cancellationToken);

                return note is null ? RegistryProblems.CustomerNoteNotFound(noteId) : Results.Ok(CustomerNoteResponse.From(note));
            })
            .RequirePermission(Permissions.Customers.Read)
            .WithName("GetCustomerNote");

        noteScoped
            .MapPut("/{noteId:guid}", ([FromRoute] Guid noteId) =>
                // Present so that the refusal can explain itself. See the class remarks: an absent
                // route answers 405 and says nothing about corrections, and this is the one rule of
                // the package a client is most likely to discover by trying.
                RegistryProblems.NoteLogIsAppendOnly(noteId))
            .RequirePermission(Permissions.Customers.Write)
            .WithName("EditCustomerNote");

        noteScoped
            .MapPost("/{noteId:guid}/corrections", (
                [FromRoute] Guid noteId,
                CorrectNoteRequest body,
                [FromServices] ICustomerNoteService notes,
                CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var correction = await notes.CorrectAsync(
                        noteId,
                        new CorrectCustomerNoteInput(
                            body.Kind,
                            body.Body,
                            body.FollowUpOn,
                            body.Link is null ? null : new CustomerNoteLinkInput(body.Link.Kind, body.Link.EntityId)),
                        cancellationToken);

                    return Results.Created($"{RoutePrefix}/{correction.Id}", CustomerNoteResponse.From(correction));
                }))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<CorrectNoteRequest>()
            .WithName("CorrectCustomerNote");

        noteScoped
            // A PUT rather than a POST sub-resource, and rather than POST/DELETE on `/pin`: pinning
            // IS setting a field to a value the caller states, it is idempotent, and it is the one
            // field on this row that a caller may set. That is what PUT is for.
            .MapPut("/{noteId:guid}/pin", (
                [FromRoute] Guid noteId,
                PinNoteRequest body,
                [FromServices] ICustomerNoteService notes,
                CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(CustomerNoteResponse.From(await notes.SetPinnedAsync(noteId, body.IsPinned, cancellationToken)))))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<PinNoteRequest>()
            .WithName("PinCustomerNote");

        return endpoints;
    }
}
