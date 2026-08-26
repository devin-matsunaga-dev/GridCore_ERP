using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Notes;

/// <summary>What a caller supplies to file a note against a bill, a payment or a work order.</summary>
/// <param name="Kind">Which register the row is in.</param>
/// <param name="EntityId">The row.</param>
public sealed record CustomerNoteLinkInput(CustomerNoteLinkKind Kind, Guid EntityId);

/// <summary>What a caller supplies to log a note or an interaction.</summary>
/// <param name="Kind">A written note, or the contact that took place.</param>
/// <param name="Body">What was said or written.</param>
/// <param name="ServiceAccountId">The account it is about, where it is about one rather than the customer.</param>
/// <param name="FollowUpOn">The day somebody has to come back to it.</param>
/// <param name="Link">What it is filed against.</param>
public sealed record LogCustomerNoteInput(
    CustomerNoteKind Kind,
    string Body,
    Guid? ServiceAccountId = null,
    DateOnly? FollowUpOn = null,
    CustomerNoteLinkInput? Link = null);

/// <summary>
/// What a caller supplies to correct an earlier note.
/// </summary>
/// <remarks>
/// The customer and the account are absent, and that is the point: a correction is filed where the
/// note it corrects was filed. Everything else is supplied afresh, the kind included.
/// </remarks>
/// <param name="Kind">What the contact actually was.</param>
/// <param name="Body">What it should have said.</param>
/// <param name="FollowUpOn">The day somebody has to come back to it.</param>
/// <param name="Link">What it is filed against.</param>
public sealed record CorrectCustomerNoteInput(
    CustomerNoteKind Kind,
    string Body,
    DateOnly? FollowUpOn = null,
    CustomerNoteLinkInput? Link = null);

/// <summary>How a caller narrows a customer's note log.</summary>
/// <param name="Kind">Only entries of this kind.</param>
/// <param name="ServiceAccountId">Only entries about this account.</param>
/// <param name="PinnedOnly">Only the entries somebody put at the top.</param>
/// <param name="Limit">The most to return.</param>
public sealed record CustomerNoteFilter(
    CustomerNoteKind? Kind = null,
    Guid? ServiceAccountId = null,
    bool PinnedOnly = false,
    int Limit = CustomerNoteService.DefaultLimit);

/// <summary>The customer's note log: what a rep wrote down, and every contact that took place.</summary>
public interface ICustomerNoteService
{
    /// <summary>One customer's log, pinned entries first and newest first within each group.</summary>
    /// <exception cref="CustomerNotFoundException">There is no such customer.</exception>
    Task<IReadOnlyList<CustomerNote>> ListAsync(
        Guid customerId,
        CustomerNoteFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>One note, or <see langword="null"/> if there is no such id.</summary>
    Task<CustomerNote?> FindAsync(Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>Logs a note or an interaction against a customer.</summary>
    Task<CustomerNote> LogAsync(Guid customerId, LogCustomerNoteInput input, CancellationToken cancellationToken = default);

    /// <summary>Writes a new note correcting an earlier one. The earlier one is never touched.</summary>
    Task<CustomerNote> CorrectAsync(Guid noteId, CorrectCustomerNoteInput input, CancellationToken cancellationToken = default);

    /// <summary>Puts a note at the top of the customer's log, or takes it back down.</summary>
    Task<CustomerNote> SetPinnedAsync(Guid noteId, bool isPinned, CancellationToken cancellationToken = default);
}

/// <summary>
/// The note log over the customers schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no update method, and that absence is the feature.</b> WORK_PACKAGES.md WP-2.13 makes
/// the log append-only, so the only ways a note's content reaches the database are
/// <see cref="LogAsync"/> and <see cref="CorrectAsync"/>, and both of them write a new row. The
/// endpoint that a client would reach for out of habit — a <c>PUT</c> on the note — exists and
/// answers 409 with the reason, rather than 405 or nothing at all: a route that is simply missing
/// tells a caller the verb is unsupported, where the truth is that this register does not work that
/// way and the correction sub-resource is what they want.
/// </para>
/// <para>
/// <b>Every write is permission-gated by the route and audited</b> — invariants 1 and 5. Logging a
/// call is clerical work, so it rides on <see cref="Permissions.Customers.Write"/> rather than
/// earning a permission of its own; that is the difference between this and WP-2.12's deposits,
/// where money changes hands. Reading is <see cref="Permissions.Customers.Read"/>, which is what a
/// rep answering the telephone already holds.
/// </para>
/// <para>
/// <b>There are no events and no new outbound seam.</b> Nothing outside Customers acts on a note —
/// the same call WP-2.11 made about contacts. A billing dispute logged here is a fact the later
/// billing-deepening pass will read through the module's own service, not one this package pushes at
/// it, and publishing would be inventing a consumer.
/// </para>
/// <para>
/// <b>A link is verified before it is stored, except a work order — see
/// <see cref="CustomerNoteLinkKinds.IsVerifiable"/>.</b> Bills are checked through
/// <see cref="IBillDirectory"/> and payments through <see cref="IPaymentDirectory"/>, both for
/// existence and for belonging to this customer. Work orders are stored as given until WP-3.1 builds
/// the register and the seam to ask it; that exception is deliberate, agreed with the owner, and
/// recorded in DECISIONS.md as well as here.
/// </para>
/// </remarks>
public sealed class CustomerNoteService(
    CustomersDbContext database,
    IBillDirectory bills,
    IPaymentDirectory payments,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider clock) : ICustomerNoteService
{
    /// <summary>The most entries a log read returns when the caller does not say.</summary>
    public const int DefaultLimit = 100;

    /// <summary>The most entries a log read will return, whatever the caller asks for.</summary>
    public const int MaxLimit = 500;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CustomerNote>> ListAsync(
        Guid customerId,
        CustomerNoteFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (!await database.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken).ConfigureAwait(false))
        {
            throw new CustomerNotFoundException(customerId);
        }

        var narrowed = filter ?? new CustomerNoteFilter();

        var query = database.CustomerNotes
            .AsNoTracking()
            .Where(note => note.CustomerId == customerId);

        if (narrowed.Kind is { } kind)
        {
            query = query.Where(note => note.Kind == kind);
        }

        if (narrowed.ServiceAccountId is { } accountId)
        {
            query = query.Where(note => note.ServiceAccountId == accountId);
        }

        if (narrowed.PinnedOnly)
        {
            query = query.Where(note => note.IsPinned);
        }

        return await query
            // Pinned first, then newest first, which is WORK_PACKAGES.md's "pinned notes sort ahead
            // of unpinned regardless of date" expressed as the query rather than as a sort in the
            // browser. It is BOTH: the host orders so that a truncated read returns the entries a
            // rep needs rather than an arbitrary window, and `notes.ts` orders again so the screen's
            // order is its own rather than something it inherits and cannot state.
            .OrderByDescending(note => note.IsPinned)

            // By key, not by RecordedAt: ids are Guid v7, so the primary-key index already orders
            // chronologically on Postgres and on the fast tier's SQLite alike, and two notes logged
            // in the same millisecond still come back in a defined order.
            .ThenByDescending(note => note.Id)
            .Take(Math.Clamp(narrowed.Limit, 1, MaxLimit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<CustomerNote?> FindAsync(Guid noteId, CancellationToken cancellationToken = default) =>
        database.CustomerNotes.AsNoTracking().FirstOrDefaultAsync(note => note.Id == noteId, cancellationToken);

    /// <inheritdoc />
    public Task<CustomerNote> LogAsync(Guid customerId, LogCustomerNoteInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                // A note against a customer who does not exist is an orphan row nothing will ever
                // read, so it is a 404 rather than a foreign-key error at commit time — the call
                // CustomerContactService already makes.
                if (!await database.Customers.AnyAsync(customer => customer.Id == customerId, ct).ConfigureAwait(false))
                {
                    throw new CustomerNotFoundException(customerId);
                }

                await RequireTheAccountIsThisCustomersAsync(customerId, input.ServiceAccountId, ct).ConfigureAwait(false);

                var link = await ResolveLinkAsync(customerId, input.Link, ct).ConfigureAwait(false);

                var note = CustomerNote.Log(
                    customerId,
                    input.ServiceAccountId,
                    input.Kind,
                    input.Body,
                    input.FollowUpOn,
                    link,
                    RegistryActor.Of(currentUser),
                    clock.GetUtcNow());

                database.CustomerNotes.Add(note);

                audit.Record(
                    AuditActions.CustomerNoteLogged,
                    AuditEntityTypes.CustomerNote,
                    note.Id.ToString(),

                    // A null `before`, always: the row did not exist a moment ago. The same shape
                    // every other create in this module audits with.
                    before: null,
                    after: CustomerNoteSnapshot.Of(note));

                return note;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<CustomerNote> CorrectAsync(Guid noteId, CorrectCustomerNoteInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var original = await database.CustomerNotes
                    .AsNoTracking()
                    .FirstOrDefaultAsync(note => note.Id == noteId, ct)
                    .ConfigureAwait(false)
                    ?? throw new CustomerNoteNotFoundException(noteId);

                var link = await ResolveLinkAsync(original.CustomerId, input.Link, ct).ConfigureAwait(false);

                var correction = CustomerNote.Correct(
                    original,
                    input.Kind,
                    input.Body,
                    input.FollowUpOn,
                    link,
                    RegistryActor.Of(currentUser),
                    clock.GetUtcNow());

                database.CustomerNotes.Add(correction);

                // Audited against the CORRECTION, with the note it replaces as the `before`. That is
                // what makes the trail read the way the register does — a new row, and the thing it
                // supersedes beside it — rather than claiming an entity was updated when none was.
                // Its own action rather than a second `customer_note.create`, because "what has been
                // corrected on this account" is a question asked by filtering.
                audit.Record(
                    AuditActions.CustomerNoteCorrected,
                    AuditEntityTypes.CustomerNote,
                    correction.Id.ToString(),
                    before: CustomerNoteSnapshot.Of(original),
                    after: CustomerNoteSnapshot.Of(correction));

                return correction;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<CustomerNote> SetPinnedAsync(Guid noteId, bool isPinned, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                var note = await database.CustomerNotes.FindAsync([noteId], ct).ConfigureAwait(false)
                    ?? throw new CustomerNoteNotFoundException(noteId);

                var before = CustomerNoteSnapshot.Of(note);

                // Idempotent: pinning a pinned note is not a conflict, so it is not audited either.
                // An entry saying the flag went from true to true is noise in the one place noise is
                // most expensive.
                if (note.SetPinned(isPinned))
                {
                    audit.Record(
                        AuditActions.CustomerNotePinned,
                        AuditEntityTypes.CustomerNote,
                        note.Id.ToString(),
                        before,
                        CustomerNoteSnapshot.Of(note));
                }

                return note;
            },
            cancellationToken);

    /// <summary>
    /// Refuses an account that is not this customer's.
    /// </summary>
    /// <remarks>
    /// Both halves are checked in one query. A note filed under somebody else's account would appear
    /// on their 360 — a disclosure, not a typo — and an id matching nothing is a mistyped request.
    /// </remarks>
    private async Task RequireTheAccountIsThisCustomersAsync(
        Guid customerId,
        Guid? serviceAccountId,
        CancellationToken cancellationToken)
    {
        if (serviceAccountId is not { } accountId)
        {
            return;
        }

        var belongs = await database.ServiceAccounts
            .AnyAsync(account => account.Id == accountId && account.CustomerId == customerId, cancellationToken)
            .ConfigureAwait(false);

        if (!belongs)
        {
            throw new RegistryValidationException(
                $"Service account '{accountId}' is not this customer's, so a note about them cannot be filed against it.");
        }
    }

    /// <summary>
    /// Turns what the caller named into a link, refusing anything that does not exist or is not this
    /// customer's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="RegistryValidationException"/> — a 400 — rather than a 404, deliberately. The
    /// thing that was not found is not the thing being addressed: the request is to write a note, and
    /// what is wrong is a field in its body. Answering 404 would tell a client the <i>customer</i>
    /// was missing, which is the resource in the URL.
    /// </para>
    /// <para>
    /// <b>The work-order branch is WP-2.13's one accepted gap.</b> It is expressed as
    /// <see cref="CustomerNoteLinkKinds.IsVerifiable"/> rather than as an <c>if kind is WorkOrder</c>
    /// here, so that WP-3.1 flipping that one method is all it takes — and so the exception is
    /// findable by whoever goes looking for why a link was not checked. A work-order link therefore
    /// carries no reference: there is no register to ask what the number is, and inventing one would
    /// put a number on screen that nothing produced.
    /// </para>
    /// </remarks>
    private async Task<CustomerNoteLink?> ResolveLinkAsync(
        Guid customerId,
        CustomerNoteLinkInput? link,
        CancellationToken cancellationToken)
    {
        if (link is null)
        {
            return null;
        }

        if (!CustomerNoteLinkKinds.IsKnown(link.Kind))
        {
            throw new RegistryValidationException($"'{(int)link.Kind}' is not something a note can be filed against.");
        }

        if (link.EntityId == Guid.Empty)
        {
            throw new RegistryValidationException($"A note linked to a {link.Kind} must name which one.");
        }

        if (!CustomerNoteLinkKinds.IsVerifiable(link.Kind))
        {
            return new CustomerNoteLink(link.Kind, link.EntityId, Reference: null);
        }

        return link.Kind switch
        {
            CustomerNoteLinkKind.Bill => await ResolveBillAsync(customerId, link.EntityId, cancellationToken).ConfigureAwait(false),
            CustomerNoteLinkKind.Payment => await ResolvePaymentAsync(customerId, link.EntityId, cancellationToken).ConfigureAwait(false),

            // Not a default that guesses. A verifiable kind added without a branch would fall through
            // to "stored unchecked", which is the exact behaviour this method exists to make
            // deliberate rather than accidental.
            _ => throw new RegistryValidationException(
                $"GridCore says it can verify a {link.Kind} link but has no way to; that is a bug, not a bad request."),
        };
    }

    private async Task<CustomerNoteLink> ResolveBillAsync(Guid customerId, Guid billId, CancellationToken cancellationToken)
    {
        var bill = await bills.FindAsync(billId, cancellationToken).ConfigureAwait(false)
            ?? throw new RegistryValidationException($"Bill '{billId}' was not found, so a note cannot be filed against it.");

        if (bill.CustomerId != customerId)
        {
            throw new RegistryValidationException(
                $"Bill {bill.BillNumber} belongs to another customer, so a note about this one cannot be filed against it.");
        }

        return new CustomerNoteLink(CustomerNoteLinkKind.Bill, bill.Id, bill.BillNumber);
    }

    private async Task<CustomerNoteLink> ResolvePaymentAsync(Guid customerId, Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await payments.FindAsync(paymentId, cancellationToken).ConfigureAwait(false)
            ?? throw new RegistryValidationException($"Payment '{paymentId}' was not found, so a note cannot be filed against it.");

        if (payment.CustomerId != customerId)
        {
            throw new RegistryValidationException(
                $"Payment {payment.PaymentNumber} belongs to another customer, so a note about this one cannot be filed against it.");
        }

        // A DECLINED payment is a perfectly good thing to file a note against — it is usually the
        // reason the customer rang. Only existence and ownership are checked here; the seam is
        // narrow on purpose (see IPaymentDirectory).
        return new CustomerNoteLink(CustomerNoteLinkKind.Payment, payment.Id, payment.PaymentNumber);
    }
}

/// <summary>
/// The shape a note is audited as.
/// </summary>
/// <remarks>
/// The whole note, because a note is small and every field of it is the point. The actor is not
/// repeated — the audit entry already carries who acted, and on a correction the two can legitimately
/// differ, which is exactly why the <c>before</c> snapshot keeps the original's.
/// </remarks>
/// <param name="Id">Identifier of the note.</param>
/// <param name="CustomerId">The customer it is about.</param>
/// <param name="ServiceAccountId">The account it is about, where it is about one.</param>
/// <param name="Kind">A written note, or the contact that took place.</param>
/// <param name="Body">What was said or written.</param>
/// <param name="FollowUpOn">The day somebody has to come back to it.</param>
/// <param name="LinkKind">Which register it is filed against.</param>
/// <param name="LinkedEntityId">The row in that register.</param>
/// <param name="LinkedReference">Its number, as printed.</param>
/// <param name="CorrectsNoteId">The note it corrects.</param>
/// <param name="IsPinned">Whether it sits at the top of the log.</param>
/// <param name="ActorId">Subject id of the rep who logged it.</param>
/// <param name="RecordedAt">When it was logged.</param>
public sealed record CustomerNoteSnapshot(
    Guid Id,
    Guid CustomerId,
    Guid? ServiceAccountId,
    CustomerNoteKind Kind,
    string Body,
    DateOnly? FollowUpOn,
    CustomerNoteLinkKind? LinkKind,
    Guid? LinkedEntityId,
    string? LinkedReference,
    Guid? CorrectsNoteId,
    bool IsPinned,
    string ActorId,
    DateTimeOffset RecordedAt)
{
    /// <summary>Takes the snapshot.</summary>
    public static CustomerNoteSnapshot Of(CustomerNote note)
    {
        ArgumentNullException.ThrowIfNull(note);

        return new CustomerNoteSnapshot(
            note.Id,
            note.CustomerId,
            note.ServiceAccountId,
            note.Kind,
            note.Body,
            note.FollowUpOn,
            note.LinkKind,
            note.LinkedEntityId,
            note.LinkedReference,
            note.CorrectsNoteId,
            note.IsPinned,
            note.ActorId,
            note.RecordedAt);
    }
}
