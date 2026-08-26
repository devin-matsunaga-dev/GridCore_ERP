namespace GridCore.Modules.Customers.Features.Notes;

/// <summary>
/// What a note is filed against, when it is filed against anything.
/// </summary>
/// <remarks>
/// WORK_PACKAGES.md WP-2.13: "an optional link to the bill, payment or work order it concerns". The
/// three live in three other modules, so the link is a <i>kind plus an id</i> rather than three
/// nullable foreign keys — a constraint across a module boundary is the coupling schema-per-module
/// exists to prevent, and three columns of which at most one may be set is a rule nothing enforces.
/// </remarks>
public enum CustomerNoteLinkKind
{
    /// <summary>A bill, in the Billing schema. Verified through <c>IBillDirectory</c>.</summary>
    Bill,

    /// <summary>A payment, in the Payments schema. Verified through <c>IPaymentDirectory</c>.</summary>
    Payment,

    /// <summary>
    /// A work order, in the WorkOrders schema.
    /// </summary>
    /// <remarks>
    /// <b>Stored and NOT verified — the one exception in this module, and a temporary one.</b> The
    /// WorkOrders module is a stub until WP-3.1 builds its core, so there is no register to ask and
    /// no <c>IWorkOrderDirectory</c> to ask it through. The alternatives were both worse: dropping
    /// the link means reshaping this table and every note written before it when WP-3.1 lands, and
    /// pretending to verify it means a check that silently passes everything. So the shape ships now
    /// and the guarantee arrives with the seam. <see cref="CustomerNoteLinkKinds.IsVerifiable"/> is
    /// where that exception is written down, and it is the one line WP-3.1 has to change.
    /// </remarks>
    WorkOrder,
}

/// <summary>What each <see cref="CustomerNoteLinkKind"/> means to the code that resolves it.</summary>
public static class CustomerNoteLinkKinds
{
    /// <summary>Every link kind GridCore declares.</summary>
    public static IReadOnlyList<CustomerNoteLinkKind> All { get; } = Enum.GetValues<CustomerNoteLinkKind>();

    /// <summary>Whether <paramref name="kind"/> is one this module declares.</summary>
    public static bool IsKnown(CustomerNoteLinkKind kind) => Enum.IsDefined(kind);

    /// <summary>
    /// Whether GridCore can currently confirm that a link of this kind points at something real.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is WP-2.13's one accepted gap, stated as code rather than left as a habit.</b> A bill
    /// link is checked against <c>IBillDirectory</c> and a payment link against
    /// <c>IPaymentDirectory</c>; a work-order link is stored as given, because
    /// <see cref="CustomerNoteLinkKind.WorkOrder"/> has no register behind it until WP-3.1.
    /// </para>
    /// <para>
    /// <b>WP-3.1's checklist is this method.</b> When an <c>IWorkOrderDirectory</c> exists, this
    /// returns <see langword="true"/> for every kind, <c>CustomerNoteService</c> gains one more
    /// branch beside the two it has, and notes written in the meantime keep working — an unverified
    /// link that turns out to name a real work order needs nothing done to it, and one that does not
    /// was always a dangling reference rather than a new one.
    /// </para>
    /// </remarks>
    public static bool IsVerifiable(CustomerNoteLinkKind kind) => kind is not CustomerNoteLinkKind.WorkOrder;
}
