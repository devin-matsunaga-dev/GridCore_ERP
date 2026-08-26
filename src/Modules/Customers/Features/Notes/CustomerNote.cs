using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.Notes;

/// <summary>
/// What a note points at, when it points at anything: which register, which row in it, and the
/// number a rep would read off the screen.
/// </summary>
/// <remarks>
/// The reference is stored beside the id for the reason <c>DepositEntry.BillNumber</c> and
/// <c>Payment.BillNumber</c> already are: a note read back two years from now has to say
/// <i>BIL-000142</i> without a cross-module lookup, and re-resolving it at read time would let the
/// note quietly change what it says. It is captured from the directory that verified the link, so it
/// is what the register itself called the row at the time.
/// </remarks>
/// <param name="Kind">Which register the row is in.</param>
/// <param name="EntityId">The row.</param>
/// <param name="Reference">Its number, as printed, where the register has one.</param>
public sealed record CustomerNoteLink(CustomerNoteLinkKind Kind, Guid EntityId, string? Reference);

/// <summary>
/// One entry in a customer's note log: something a rep wrote down, or a contact that took place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only, and that is the work package's central rule.</b> WORK_PACKAGES.md WP-2.13: "notes
/// are append-only — a correction is a new note referencing the old, never an overwrite". A service
/// record somebody can edit is a service record that cannot be relied on in a dispute, which is the
/// only situation anybody reads one in. So there is no method here that changes what a note says:
/// <see cref="Correct"/> mints a <i>new</i> note carrying <see cref="CorrectsNoteId"/>, and the
/// original stays exactly as it was written.
/// </para>
/// <para>
/// <b><see cref="IsPinned"/> is the one thing that moves, and it is deliberately not content.</b>
/// Pinning is a shelf, not a sentence — it decides where a note sits on a screen and says nothing
/// about what happened. Everything a reader would quote in an argument (the words, the kind, the
/// instant, who logged it, what it links to, when it needs following up) is <c>private init</c> and
/// unreachable after the row is written. The distinction is worth being explicit about, because
/// "append-only except one column" is exactly the kind of exception that grows a second member.
/// </para>
/// <para>
/// <b>No back-pointer from the corrected note to its correction.</b> Recording one would mean
/// writing to a row that is supposed to be immutable, to record something a reader can already
/// derive: the corrections carry <see cref="CorrectsNoteId"/>, and a screen holding the customer's
/// notes knows which of them supersede which. The rule stays absolute at the cost of one grouping in
/// the browser, which is the right trade — an exception "just for bookkeeping" is how the rule ends.
/// </para>
/// <para>
/// <b>An aggregate root, not a child of <c>Customer</c>.</b> It names a customer by id, the way
/// <c>ServiceAccount</c>, <c>CustomerContact</c> and <c>DepositEntry</c> do, so the registry list,
/// WP-2.9's search and the 360's customer query are untouched by this work package — none of them
/// wants to load years of call notes to render a row.
/// </para>
/// </remarks>
public sealed class CustomerNote
{
    /// <summary>Longest stored form of a kind name.</summary>
    public const int KindNameLength = 32;

    /// <summary>
    /// Longest note stored. Generous: this is the field a rep types a conversation into, and a
    /// truncated account of a complaint is worse than a long one.
    /// </summary>
    public const int BodyLength = 4000;

    /// <summary>Longest reference stored against a link — a bill or payment number.</summary>
    public const int ReferenceLength = RegistryNumbers.MaxLength;

    private CustomerNote()
    {
        // EF materialisation.
        Body = string.Empty;
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this note. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The customer the note is about.</summary>
    public Guid CustomerId { get; private init; }

    /// <summary>
    /// The service account it is about, where it is about one rather than about the customer.
    /// </summary>
    /// <remarks>
    /// Nullable because both are ordinary. "Rang about the bill on A-000012" belongs to the account;
    /// "will not accept calls before 10am" belongs to the person and would be wrong filed under
    /// whichever of their three supplies happened to be open at the time.
    /// </remarks>
    public Guid? ServiceAccountId { get; private init; }

    /// <summary>What this entry is — a written note, or the contact that took place.</summary>
    public CustomerNoteKind Kind { get; private init; }

    /// <summary>What was said or written, in the rep's own words.</summary>
    public string Body { get; private init; }

    /// <summary>
    /// The day somebody has to come back to this, where one was set.
    /// </summary>
    /// <remarks>
    /// A <see cref="DateOnly"/> rather than an instant: "ring them back on Thursday" is a day's work,
    /// and storing 14:32:07 against it would invent a precision nobody chose. The same call
    /// <c>Bill.DueDate</c> makes.
    /// </remarks>
    public DateOnly? FollowUpOn { get; private init; }

    /// <summary>Which register this note is filed against, or <see langword="null"/> when it is filed against none.</summary>
    public CustomerNoteLinkKind? LinkKind { get; private init; }

    /// <summary>The row in that register.</summary>
    public Guid? LinkedEntityId { get; private init; }

    /// <summary>Its number, as printed, kept so the note reads without a cross-module lookup.</summary>
    public string? LinkedReference { get; private init; }

    /// <summary>
    /// The note this one corrects, or <see langword="null"/> when it is not a correction.
    /// </summary>
    /// <remarks>
    /// Correcting a correction is allowed and points at the correction, not at the original — a chain
    /// read end to end is the honest record of somebody getting it wrong twice, and collapsing it to
    /// the first note would lose the middle version that a customer may have been quoted from.
    /// </remarks>
    public Guid? CorrectsNoteId { get; private init; }

    /// <summary>Whether the note sits at the top of the customer's log regardless of its age.</summary>
    public bool IsPinned { get; private set; }

    /// <summary>Subject id of the rep who logged it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>When it was logged.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>Whether this entry records a contact that took place rather than something written down.</summary>
    public bool IsInteraction => CustomerNoteKinds.IsInteraction(Kind);

    /// <summary>Whether this note corrects an earlier one.</summary>
    public bool IsCorrection => CorrectsNoteId is not null;

    /// <summary>What the note points at, or <see langword="null"/> when it points at nothing.</summary>
    /// <remarks>
    /// The three columns put back together. They are written as a set and read as a set, and this is
    /// what stops a caller finding a <see cref="LinkedEntityId"/> without the
    /// <see cref="LinkKind"/> that says which register to look in.
    /// </remarks>
    public CustomerNoteLink? Link =>
        LinkKind is { } kind && LinkedEntityId is { } id ? new CustomerNoteLink(kind, id, LinkedReference) : null;

    /// <summary>Logs a note or an interaction against <paramref name="customerId"/>.</summary>
    /// <param name="customerId">The customer it is about.</param>
    /// <param name="serviceAccountId">The account it is about, where it is about one.</param>
    /// <param name="kind">A written note, or the contact that took place.</param>
    /// <param name="body">What was said or written.</param>
    /// <param name="followUpOn">The day somebody has to come back to it, where one was set.</param>
    /// <param name="link">What it is filed against, already verified by the caller.</param>
    /// <param name="actor">Who logged it.</param>
    /// <param name="now">The clock, for the row's own identity, its timestamp and the follow-up guard.</param>
    /// <exception cref="RegistryValidationException">
    /// The customer is missing, the kind is not one GridCore declares, the body is empty, or the
    /// follow-up date is in the past.
    /// </exception>
    public static CustomerNote Log(
        Guid customerId,
        Guid? serviceAccountId,
        CustomerNoteKind kind,
        string body,
        DateOnly? followUpOn,
        CustomerNoteLink? link,
        RegistryActor actor,
        DateTimeOffset now) =>
        Record(customerId, serviceAccountId, kind, body, followUpOn, link, correctsNoteId: null, actor, now);

    /// <summary>
    /// Corrects <paramref name="original"/> by writing a new note that references it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The original is not touched, and this method could not touch it if it wanted to.</b> That
    /// is the whole point of the register: whoever reads the log sees what was first written and
    /// what replaced it, in that order, with the same rep's name against whichever of them they
    /// actually wrote.
    /// </para>
    /// <para>
    /// The correction takes the original's customer and its account, because a correction that could
    /// re-file a note under a different customer is not a correction — it is a second note, and the
    /// caller should log one. Everything else is supplied afresh, including the kind: "logged as an
    /// inbound call, it was actually a counter visit" is exactly the sort of thing a correction is
    /// for.
    /// </para>
    /// </remarks>
    /// <exception cref="RegistryValidationException">
    /// The kind is not one GridCore declares, the body is empty, or the follow-up date is in the past.
    /// </exception>
    public static CustomerNote Correct(
        CustomerNote original,
        CustomerNoteKind kind,
        string body,
        DateOnly? followUpOn,
        CustomerNoteLink? link,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(original);

        return Record(
            original.CustomerId,
            original.ServiceAccountId,
            kind,
            body,
            followUpOn,
            link,
            correctsNoteId: original.Id,
            actor,
            now);
    }

    /// <summary>
    /// Puts the note at the top of the customer's log, or takes it back down.
    /// </summary>
    /// <remarks>
    /// The only state on this type that moves, and it moves nothing a reader would quote — see the
    /// note on the class. Idempotent on purpose: two reps pinning the same note is not a conflict,
    /// and answering the second with a 409 would be inventing one.
    /// </remarks>
    /// <returns>Whether the flag actually changed, so a caller can skip auditing a no-op.</returns>
    public bool SetPinned(bool isPinned)
    {
        if (IsPinned == isPinned)
        {
            return false;
        }

        IsPinned = isPinned;

        return true;
    }

    /// <summary>Builds a note, applying every guard before any of it is assembled.</summary>
    private static CustomerNote Record(
        Guid customerId,
        Guid? serviceAccountId,
        CustomerNoteKind kind,
        string body,
        DateOnly? followUpOn,
        CustomerNoteLink? link,
        Guid? correctsNoteId,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (customerId == Guid.Empty)
        {
            throw new RegistryValidationException("A note must name the customer it is about.");
        }

        if (!CustomerNoteKinds.IsKnown(kind))
        {
            throw new RegistryValidationException(
                $"'{(int)kind}' is not a note kind GridCore declares. A logged interaction must say what kind of contact it was.");
        }

        // The body is the note. An entry with nothing in it records that somebody pressed a button,
        // which is not a fact about the customer.
        var text = RegistryText.Clean(body, BodyLength)
            ?? throw new RegistryValidationException("A note must say something; an empty note records nothing.");

        RequireTheFollowUpIsNotInThePast(followUpOn, now);

        if (link is not null && link.EntityId == Guid.Empty)
        {
            throw new RegistryValidationException($"A note linked to a {link.Kind} must name which one.");
        }

        if (link is not null && !CustomerNoteLinkKinds.IsKnown(link.Kind))
        {
            throw new RegistryValidationException($"'{(int)link.Kind}' is not something a note can be filed against.");
        }

        return new CustomerNote
        {
            Id = Guid.CreateVersion7(now),
            CustomerId = customerId,
            ServiceAccountId = serviceAccountId,
            Kind = kind,
            Body = text,
            FollowUpOn = followUpOn,
            LinkKind = link?.Kind,
            LinkedEntityId = link?.EntityId,
            LinkedReference = RegistryText.Clean(link?.Reference, ReferenceLength),
            CorrectsNoteId = correctsNoteId,
            IsPinned = false,
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new RegistryValidationException("A note must name who logged it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            RecordedAt = now,
        };
    }

    /// <summary>
    /// Refuses a follow-up somebody can never act on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WORK_PACKAGES.md WP-2.13: "follow-up date cannot be in the past". <b>Today is allowed</b> —
    /// "ring them back this afternoon" is the commonest follow-up there is, and a rule that refused
    /// it would read as an off-by-one to every rep who hit it.
    /// </para>
    /// <para>
    /// Compared against the UTC date, which is the same clock every other date in GridCore is stamped
    /// from. It can differ from the rep's own calendar day by a few hours either side of midnight,
    /// and the honest options were this or storing a time zone per user — a real feature with real
    /// consequences for bills and readings, not something to invent inside a notes package. The
    /// guard is a floor, so the disagreement can only ever admit a follow-up a few hours early, never
    /// refuse one a rep meant.
    /// </para>
    /// <para>
    /// The guard is here rather than in the validator because the validator cannot see the clock, and
    /// because a seeder or a later module calling the service directly must meet the same rule —
    /// the call <c>MeterReading</c> and <c>Asset</c> already make about a date in the future.
    /// </para>
    /// </remarks>
    private static void RequireTheFollowUpIsNotInThePast(DateOnly? followUpOn, DateTimeOffset now)
    {
        if (followUpOn is not { } date)
        {
            return;
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);

        if (date < today)
        {
            throw new RegistryValidationException(
                $"A follow-up cannot be set for '{date:O}', which is before {today:O}. "
                + "Record what happened in the note; a follow-up is something somebody still has to do.");
        }
    }
}
