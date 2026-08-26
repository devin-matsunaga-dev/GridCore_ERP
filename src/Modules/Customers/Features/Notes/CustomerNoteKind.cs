namespace GridCore.Modules.Customers.Features.Notes;

/// <summary>
/// What one entry in a customer's note log is.
/// </summary>
/// <remarks>
/// <para>
/// <b>One set, not two.</b> WORK_PACKAGES.md WP-2.13 asks for "free-text notes and structured logged
/// interactions", and it would have been possible to build those as two tables. They are one,
/// because what a rep actually wants is the single thread of everything said to and about this
/// customer — and splitting it would mean interleaving two queries on screen to rebuild it. That is
/// the call <c>AssetHistoryEntryType</c> already made for a technician's maintenance history
/// (DECISIONS.md, WP-1.3), and it is the same reasoning: the <i>kind</i> tells the lines apart, and
/// a filter narrows them.
/// </para>
/// <para>
/// <see cref="Note"/> is the unstructured one and every other member is a contact that took place.
/// <see cref="CustomerNoteKinds.IsInteraction"/> is where that distinction is drawn, once — a screen
/// asking "was this a conversation" must not do it by listing six members, because the seventh is
/// added without it.
/// </para>
/// <para>
/// Adding a member means a place in this enum and an entry in the browser's <c>noteKinds</c>. No
/// migration: the column stores the name, so the numbering here is never load-bearing.
/// </para>
/// </remarks>
public enum CustomerNoteKind
{
    /// <summary>
    /// Something a rep wrote down that was not a conversation — a standing instruction, a note about
    /// access to the premise, the reason an account is being watched.
    /// </summary>
    Note,

    /// <summary>The customer rang the utility.</summary>
    InboundCall,

    /// <summary>The utility rang the customer.</summary>
    OutboundCall,

    /// <summary>They came to the counter.</summary>
    CounterVisit,

    /// <summary>Somebody went out to them.</summary>
    FieldVisit,

    /// <summary>
    /// A grievance, whatever channel it arrived by. Its own kind rather than a flag on a call,
    /// because "how many complaints has this customer raised" is a question asked by filtering.
    /// </summary>
    Complaint,

    /// <summary>
    /// A challenge to what was billed. The kind that most often carries a link, and the reason
    /// WP-2.13 exists at all for the later billing pass: it is what makes "why was this bill
    /// adjusted" answerable from the customer's side rather than only from the adjustment's.
    /// </summary>
    BillingDispute,
}

/// <summary>What each <see cref="CustomerNoteKind"/> means to the code that has to tell them apart.</summary>
public static class CustomerNoteKinds
{
    /// <summary>Every kind GridCore declares, in the order they read.</summary>
    public static IReadOnlyList<CustomerNoteKind> All { get; } = Enum.GetValues<CustomerNoteKind>();

    /// <summary>
    /// Whether <paramref name="kind"/> records a contact that took place, as opposed to something a
    /// rep simply wrote down.
    /// </summary>
    /// <remarks>
    /// Expressed as "not a plain note" rather than as a list of the six interactions, so a kind added
    /// later joins the set without this line being remembered — the same shape
    /// <c>ServiceAccountDirectory.Open()</c> uses for "not Closed".
    /// </remarks>
    public static bool IsInteraction(CustomerNoteKind kind) => kind is not CustomerNoteKind.Note;

    /// <summary>Whether <paramref name="kind"/> is one this module declares.</summary>
    /// <remarks>
    /// A cast from an undeclared integer produces a value the compiler is perfectly happy with —
    /// <c>(CustomerNoteKind)99</c> is a legal expression — so a body arriving off the wire is checked
    /// rather than trusted. This is what WORK_PACKAGES.md's "interaction requires a valid type" is
    /// asking for.
    /// </remarks>
    public static bool IsKnown(CustomerNoteKind kind) => Enum.IsDefined(kind);
}
