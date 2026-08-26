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
/// One outstanding bill as an arrears picture reads it: what is still owed on it, when it fell due,
/// and how long ago that was (WP-2.19).
/// </summary>
/// <remarks>
/// <b>Deliberately thinner than <see cref="BillSummary"/>.</b> An arrears line answers "how old is
/// this debt and how much of it is there", which is three columns; the summary answers "may this
/// bill take a payment", which needs the printed total, the customer and the lifecycle status.
/// Reusing the summary here would put a decade of unrelated columns behind a dunning screen and
/// invite the next reader to check a deposit offset against the wrong figure.
/// </remarks>
/// <param name="Id">Identifier of the bill, in the Billing schema.</param>
/// <param name="BillNumber">The number printed on it, e.g. <c>BIL-000001</c>.</param>
/// <param name="DueDate">The day payment fell due.</param>
/// <param name="Balance">What is still owed on it.</param>
/// <param name="DaysPastDue">
/// Days between <see cref="DueDate"/> and the day the picture was taken. <b>Zero on a bill that is
/// not yet due</b>, never negative: "minus nine days overdue" is not a thing a rep says, and a
/// negative here would sum into an ageing bucket that means nothing.
/// </param>
/// <param name="IsPastDue">Whether the due date has passed. What separates arrears from a balance.</param>
public sealed record ArrearsBill(
    Guid Id,
    string BillNumber,
    DateOnly? DueDate,
    decimal Balance,
    int DaysPastDue,
    bool IsPastDue);

/// <summary>
/// One age band of an arrears picture — the row of a debtors' ageing, which is how every utility
/// reads what it is owed.
/// </summary>
/// <param name="Label">What the band is called, e.g. <c>31-60 days</c>. Billing's wording, so a screen never invents one.</param>
/// <param name="FromDays">The fewest days past due that fall in the band. Zero on the not-yet-due band.</param>
/// <param name="ToDays">The most, or <see langword="null"/> on the open-ended oldest band.</param>
/// <param name="Amount">What is owed in it.</param>
public sealed record ArrearsBucket(string Label, int FromDays, int? ToDays, decimal Amount);

/// <summary>
/// What one service account owes, aged (WP-2.19) — the figure a late charge is taken on, a dunning
/// notice is served over and a disconnection is judged against.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="PastDueAmount"/> is not <see cref="OutstandingAmount"/>, and the whole package
/// turns on the difference.</b> A bill issued on the 28th and due on the 19th of next month is money
/// the utility is owed and is not money the customer is late with. The 1% late charge is taken on
/// the past-due figure, and a disconnection threshold read against the outstanding one would
/// disconnect a customer whose only sin is that the post has not arrived yet.
/// </para>
/// <para>
/// <b>It names no customer and no account number, deliberately.</b> Every caller already holds the
/// account it asked about — Customers owns the register the number is printed in — and a directory
/// that echoed the identity back would be a directory whose answer for an account that has never
/// been billed had to invent one.
/// </para>
/// <para>
/// <b>A picture on a stated day, not "now".</b> <see cref="AsOf"/> is what every day count is
/// measured from, so a late-charge run for last month and a screen opened today ask the same
/// question of the same register and get answers they can each defend.
/// </para>
/// </remarks>
/// <param name="ServiceAccountId">The account owing it.</param>
/// <param name="Currency">
/// ISO 4217 code every amount is expressed in — read off the account's own outstanding bills, and
/// the module's shipped code where there are none to read one off.
/// </param>
/// <param name="AsOf">The day the picture was taken. Every day count is measured from it.</param>
/// <param name="OutstandingAmount">Everything still owed, due or not.</param>
/// <param name="PastDueAmount">The part of it whose due date has passed. What arrears means.</param>
/// <param name="CurrentAmount">The rest — issued, owed, and not yet late.</param>
/// <param name="OldestDueDate">The due date of the oldest past-due bill, or <see langword="null"/> where there is none.</param>
/// <param name="DaysPastDue">How late the oldest past-due bill is. Zero where nothing is past due.</param>
/// <param name="Buckets">The ageing, oldest band last.</param>
/// <param name="Bills">The outstanding bills behind the figures, oldest due date first.</param>
public sealed record AccountArrears(
    Guid ServiceAccountId,
    string Currency,
    DateOnly AsOf,
    decimal OutstandingAmount,
    decimal PastDueAmount,
    decimal CurrentAmount,
    DateOnly? OldestDueDate,
    int DaysPastDue,
    IReadOnlyList<ArrearsBucket> Buckets,
    IReadOnlyList<ArrearsBill> Bills)
{
    /// <summary>Whether the customer is late with anything at all.</summary>
    public bool IsInArrears => PastDueAmount > 0m;
}

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

    /// <summary>
    /// What <paramref name="serviceAccountId"/> owes on <paramref name="asOf"/>, aged (WP-2.19).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The third widening, and the first that is not about one bill.</b> WP-2.5 asked "how much
    /// is owed on this bill", WP-2.14 asked "what has this customer been billed"; this asks "how
    /// late is this account", which is a question about the register rather than about a row. It
    /// lives here rather than in a fifth seam because the answer is composed from the same bills the
    /// other calls return, and a directory per question is how a module ends up with four ways to
    /// read one table.
    /// </para>
    /// <para>
    /// <b>The ageing is Billing's, not the caller's.</b> Which bands exist, what they are called and
    /// which side of a boundary a bill falls on are decisions about a debtors' ageing, and a caller
    /// handed the raw bills would be a caller free to answer them differently from the late-charge
    /// run that reads the same register. Customers consumes this to decide whether a supply may be
    /// cut off; the arithmetic behind that had better be one implementation.
    /// </para>
    /// <para>
    /// Drafts and cancelled bills are absent, for the reason <see cref="OutstandingForAccountAsync"/>
    /// gives: a draft is owed by nobody and a withdrawn bill is owed by nobody any more.
    /// </para>
    /// </remarks>
    /// <param name="serviceAccountId">The account asked about.</param>
    /// <param name="asOf">The day to age against. Every day count on the answer is measured from it.</param>
    Task<AccountArrears> ArrearsForAccountAsync(
        Guid serviceAccountId,
        DateOnly asOf,
        CancellationToken cancellationToken = default);
}
