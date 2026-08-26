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
/// One correction made to a bill, as a document outside Billing reads it (WP-2.14).
/// </summary>
/// <remarks>
/// A statement has to show a credit on the day it was granted rather than folded into the charge it
/// corrects, which is the same rule a reprint follows for the same reason: the customer holds a copy
/// of what the bill said, and a document that quietly nets the two disagrees with the paper in their
/// hand.
/// </remarks>
/// <param name="Id">Identifier of the correction, in the Billing schema.</param>
/// <param name="Sequence">Its position in the bill's adjustment history, from 1.</param>
/// <param name="Kind">Money off the bill or money on to it, by name — Contracts takes no dependency on the module's enum.</param>
/// <param name="Amount">The signed change to what is owed. Negative on a credit.</param>
/// <param name="AmountDueAfter">What the bill came to once it was applied.</param>
/// <param name="Reason">Why it was made. A statement prints this.</param>
/// <param name="RecordedAt">When it was made — the day it lands on a statement.</param>
public sealed record BillCorrection(
    Guid Id,
    int Sequence,
    string Kind,
    decimal Amount,
    decimal AmountDueAfter,
    string Reason,
    DateTimeOffset RecordedAt);

/// <summary>
/// A bill that was actually issued, with its corrections and how it ended — what an account
/// statement needs of the billing register (WP-2.14).
/// </summary>
/// <remarks>
/// <para>
/// <b>Facts, not statement lines.</b> Billing states what it did and when; turning that into an
/// opening balance, a run of dated movements and a closing balance is the Customers module's work,
/// because a statement spans this register, the payment register and a deposit ledger Billing has
/// never heard of. A <c>StatementLine</c> here would make Billing the author of a document it can
/// only write a third of.
/// </para>
/// <para>
/// <b>Only bills that were issued.</b> A draft is not owed by anybody, so it has never moved a
/// customer's balance and has no business on a statement — which is why <see cref="IssuedOn"/> is
/// not nullable here while <c>Bill.IssuedOn</c> is.
/// </para>
/// <para>
/// <b>A withdrawn bill is reported, not omitted.</b> Cancelling an issued bill takes back money the
/// utility was owed, and a statement that simply dropped it would show a charge in one period that
/// nothing ever reverses. <see cref="WithdrawnAt"/> is when that happened, and what it takes back is
/// the balance the bill was carrying — the figures below are all a reader needs to work it out.
/// </para>
/// </remarks>
/// <param name="Id">Identifier of the bill, in the Billing schema.</param>
/// <param name="BillNumber">The number printed on it, e.g. <c>BIL-000001</c>.</param>
/// <param name="ServiceAccountId">The account billed.</param>
/// <param name="AccountNumber">Its number, as printed.</param>
/// <param name="Currency">ISO 4217 code every amount on it is expressed in.</param>
/// <param name="IssuedOn">The day it went out. The day the charge lands on a statement.</param>
/// <param name="DueDate">The day payment falls due.</param>
/// <param name="PeriodStart">First day of the billed period.</param>
/// <param name="PeriodEnd">Last day of it.</param>
/// <param name="TotalAmount">What the rate engine printed. Never moves once the bill is calculated.</param>
/// <param name="AdjustmentTotal">The signed sum of <see cref="Corrections"/>.</param>
/// <param name="AmountPaid">How much has been paid against it, by cash or out of a deposit.</param>
/// <param name="Status">Where the bill stands, by name.</param>
/// <param name="WithdrawnAt">When it was cancelled, or <see langword="null"/> if it was not.</param>
/// <param name="Corrections">Its corrections, oldest first.</param>
public sealed record BillActivity(
    Guid Id,
    string BillNumber,
    Guid ServiceAccountId,
    string AccountNumber,
    string Currency,
    DateOnly IssuedOn,
    DateOnly? DueDate,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal TotalAmount,
    decimal AdjustmentTotal,
    decimal AmountPaid,
    string Status,
    DateTimeOffset? WithdrawnAt,
    IReadOnlyList<BillCorrection> Corrections);

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
/// <para>
/// <b>WP-2.14 widened it once, for the account statement.</b> Customers composes a statement across
/// this register, the payment register and its own deposit ledger, and it may read none of them
/// directly — so <see cref="ActivityForCustomerAsync"/> hands over the dated facts Billing owns and
/// nothing more. Everything else here still answers "what is owed on this bill"; the two questions
/// are kept apart deliberately, because a summary is what a payment is checked against and an
/// activity record is what a document is printed from.
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

    /// <summary>
    /// Every bill of <paramref name="customerId"/>'s that was issued on or before
    /// <paramref name="issuedOnOrBefore"/>, oldest first, each with its corrections (WP-2.14).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The whole history, not a window, and that is the point.</b> An account statement's opening
    /// balance is what every movement before the range adds up to, so a call that returned only the
    /// recent bills would produce a statement that proves out against itself and is wrong. The cap
    /// is therefore a caller-stated <paramref name="limit"/> rather than a page size, and a caller
    /// that receives exactly that many rows has to assume the history did not fit — see
    /// <c>AccountStatement.IsTruncated</c>, which is what says so on the document rather than
    /// letting a screen quietly print a short opening balance.
    /// </para>
    /// <para>
    /// Drafts are absent: a draft is owed by nobody and has never moved a balance. Cancelled bills
    /// are present, because a withdrawal takes back money the customer was told they owed and a
    /// statement has to show that happening.
    /// </para>
    /// </remarks>
    /// <param name="customerId">Whose bills.</param>
    /// <param name="issuedOnOrBefore">The last day of the statement, so nothing after it is fetched.</param>
    /// <param name="limit">Most bills to return.</param>
    Task<IReadOnlyList<BillActivity>> ActivityForCustomerAsync(
        Guid customerId,
        DateOnly issuedOnOrBefore,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The day <paramref name="customerId"/>'s most recently issued bill went out, or
    /// <see langword="null"/> if none ever has (WP-2.15).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One row, and a different question from <see cref="ActivityForCustomerAsync"/>.</b> That
    /// call hands over a whole history because a statement's opening balance is what every movement
    /// before it adds up to; this one answers "how far back may a class change be dated", which is a
    /// single date. Asking it through the history would fetch a decade of bills and their corrections
    /// to read one column off the last of them.
    /// </para>
    /// <para>
    /// <b>Issued bills only, for the reason the activity record gives.</b> A draft has never been
    /// priced at a customer, so re-classifying behind one changes nothing anybody has seen — while a
    /// bill that went out was raised on the class the customer held that day, and a class change
    /// dated before it would mean the utility had charged the wrong tariff and not said so.
    /// Cancelled bills count: a withdrawn bill still went out.
    /// </para>
    /// </remarks>
    /// <param name="customerId">Whose bills.</param>
    Task<DateOnly?> LastIssuedOnForCustomerAsync(Guid customerId, CancellationToken cancellationToken = default);
}
