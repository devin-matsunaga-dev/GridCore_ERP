namespace GridCore.Contracts.Directories;

/// <summary>
/// A bill as another module sees it: whose it is, what is still owed on it, and whether it is in a
/// state that may be paid. Nothing more.
/// </summary>
/// <remarks>
/// <para>
/// A DTO, never the entity — the rule every directory in this folder follows. <c>Bill</c> is an EF
/// type in the billing schema with its lines and its adjustment history hanging off it, and handing
/// it across the boundary would let a caller walk into tables it must never read.
/// </para>
/// <para>
/// <b><see cref="Balance"/>, not the printed total.</b> Since WP-2.4 a bill's total and what a
/// customer owes are two different figures: <see cref="TotalAmount"/> is what the rate engine
/// printed and never moves again, while the balance is that plus every correction since, less what
/// has been paid. A caller taking money must use the balance — the printed total is on this record
/// only so a receipt can show what the document said.
/// </para>
/// </remarks>
/// <param name="Id">Identifier of the bill, in the Billing schema.</param>
/// <param name="BillNumber">The number printed on it, e.g. <c>BIL-000001</c>.</param>
/// <param name="ServiceAccountId">The account billed.</param>
/// <param name="AccountNumber">Its number, as printed.</param>
/// <param name="CustomerId">Who owes it.</param>
/// <param name="CustomerName">Their name at the time it was raised.</param>
/// <param name="Currency">ISO 4217 code every amount on it is expressed in.</param>
/// <param name="TotalAmount">What the rate engine printed. Never moves once the bill is calculated.</param>
/// <param name="AmountDue">What is owed today — the printed total plus every correction since.</param>
/// <param name="AmountPaid">How much has been paid against it.</param>
/// <param name="Balance">What is still owed. The figure a payment is checked against.</param>
/// <param name="Status">Where the bill stands, by name — Contracts takes no dependency on the module's enum.</param>
/// <param name="IsOutstanding">
/// Whether the utility is still owed money on it. Decided by Billing, because the rule belongs to
/// the lifecycle that module owns — a draft is not owed by anybody and a cancelled bill never will
/// be.
/// </param>
/// <param name="DueDate">The day payment falls due, or <see langword="null"/> while it is a draft.</param>
public sealed record BillSummary(
    Guid Id,
    string BillNumber,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    string CustomerName,
    string Currency,
    decimal TotalAmount,
    decimal AmountDue,
    decimal AmountPaid,
    decimal Balance,
    string Status,
    bool IsOutstanding,
    DateOnly? DueDate);

/// <summary>
/// Read access to the billing register for modules that are not Billing.
/// </summary>
/// <remarks>
/// <para>
/// The fourth cross-module read seam in GridCore, shaped exactly like
/// <see cref="IServiceAccountDirectory"/> and <see cref="IMeterReadingDirectory"/>: the interface
/// lives in <c>Contracts</c>, the Billing module registers the implementation, and a consumer takes
/// the dependency without ever learning that a <c>billing</c> schema exists.
/// </para>
/// <para>
/// Payments (WP-2.5) is the first consumer and needs it for one question asked before any money is
/// taken: <i>how much is actually owed on this bill</i>. A payment larger than the balance is
/// refused at the edge rather than authorised and then found to be unusable — the utility must not
/// take money the bill cannot accept, because a bill that quietly swallowed the difference would
/// leave a credit with no record of where it went, and a credit balance is Finance's to hold.
/// </para>
/// <para>
/// <b>Read-only, and pointedly so.</b> Reducing a balance is <c>Bill.RecordPayment</c>, inside
/// Billing, reached only by consuming <c>PaymentApproved</c> — a second module that could write to
/// a bill is a second module that owns it. The split is deliberate: Payments asks what is owed,
/// takes the money, and states the fact; Billing decides what that fact does to the document.
/// </para>
/// </remarks>
public interface IBillDirectory
{
    /// <summary>One bill, or <see langword="null"/> when there is no such id.</summary>
    Task<BillSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The bills among <paramref name="ids"/> that exist, keyed by id. Ids that match nothing are
    /// simply absent — a caller rendering a list has to cope with one it cannot resolve anyway.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, BillSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The bills on <paramref name="serviceAccountId"/> that are still owed, newest first — the
    /// account's AR worklist, which is what a clerk taking a payment picks from.
    /// </summary>
    Task<IReadOnlyList<BillSummary>> OutstandingForAccountAsync(
        Guid serviceAccountId,
        int limit,
        CancellationToken cancellationToken = default);
}
