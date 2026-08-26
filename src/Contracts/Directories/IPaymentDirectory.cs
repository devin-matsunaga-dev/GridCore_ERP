namespace GridCore.Contracts.Directories;

/// <summary>
/// A payment as another module sees it: whose it is, what it settled, and whether the utility
/// actually holds the money. Nothing more.
/// </summary>
/// <remarks>
/// <para>
/// A DTO, never the entity — the rule every directory in this folder follows. <c>Payment</c> is an
/// EF type in the payments schema carrying the provider's verbatim answer, its reference and the
/// instrument charged, and handing it across the boundary would let a caller walk into tables it
/// must never read.
/// </para>
/// <para>
/// <b>Deliberately narrower than <see cref="IBillDirectory"/>.</b> That seam exists so a caller can
/// work out how much may be taken, which is a computation with a rule behind it. This one exists so
/// a caller can ask <i>does this payment exist, and is it this customer's</i> — the question a note
/// linking to a payment asks (WP-2.13). Nothing here is a balance and nothing here decides anything.
/// </para>
/// <para>
/// <b>WP-2.14 is the work package that widened it</b>, exactly as the note left standing here said
/// one would have to: <see cref="Method"/> and <see cref="AnsweredAt"/> arrived together with
/// <c>ForCustomerAsync</c>, because a payment-history export has to say how somebody paid and an
/// account statement has to credit the money on the day it landed. It is still not a balance —
/// what a payment did to a bill is <c>IBillDirectory</c>'s answer, and this one only ever says what
/// was tendered and what became of it.
/// </para>
/// </remarks>
/// <param name="Id">Identifier of the payment, in the Payments schema.</param>
/// <param name="PaymentNumber">The number on the receipt, e.g. <c>PAY-000001</c>.</param>
/// <param name="CustomerId">Who paid.</param>
/// <param name="ServiceAccountId">The account credited.</param>
/// <param name="BillId">The bill it was taken against.</param>
/// <param name="Amount">How much was asked for. What was <i>taken</i> is this, or nothing at all.</param>
/// <param name="Currency">ISO 4217 code the amount is expressed in.</param>
/// <param name="Status">
/// Where the attempt stands, by name — Contracts takes no dependency on the module's enum.
/// </param>
/// <param name="IsSettled">
/// Whether the utility actually holds this money. Decided by Payments, because the rule belongs to
/// the lifecycle that module owns — a declined attempt is still a payment worth linking a note to,
/// and it is emphatically not money received.
/// </param>
/// <param name="Method">
/// How it was tendered — <c>card</c>, <c>bank-transfer</c>, <c>cash</c>. The method, never the
/// instrument: "Card" is what a receipt and an export may say, and the masked card itself stays
/// inside Payments with the provider reference.
/// </param>
/// <param name="RequestedAt">When it was taken.</param>
/// <param name="AnsweredAt">
/// When the provider answered — <b>whatever it answered</b> — or <see langword="null"/> while it has
/// not. Read with <see cref="IsSettled"/> it is the day the money landed, which is the day a
/// statement credits a payment on: one attempted on the last day of a period and approved on the
/// first day of the next belongs to the period it was approved in, because that is when what the
/// customer owed changed. On a refusal it is the day the refusal came back, which is what dates that
/// row on an export. Deliberately not called <c>SettledAt</c> at this seam, though that is the
/// column behind it: a name that says "settled" on a declined payment is a name a caller will read
/// as money.
/// </param>
public sealed record PaymentSummary(
    Guid Id,
    string PaymentNumber,
    Guid CustomerId,
    Guid ServiceAccountId,
    Guid BillId,
    decimal Amount,
    string Currency,
    string Status,
    bool IsSettled,
    string Method,
    DateTimeOffset RequestedAt,
    DateTimeOffset? AnsweredAt);

/// <summary>
/// Read access to the payment register for modules that are not Payments.
/// </summary>
/// <remarks>
/// <para>
/// The fifth cross-module read seam in GridCore, shaped exactly like <see cref="IBillDirectory"/>:
/// the interface lives in <c>Contracts</c>, the Payments module registers the implementation, and a
/// consumer takes the dependency without ever learning that a <c>payments</c> schema exists.
/// </para>
/// <para>
/// Customers (WP-2.13) is the first consumer, and needs it for one question asked before a note is
/// written: <i>is the payment this note is filed against a real payment of this customer's</i>. A
/// note pointing at a payment that does not exist is a link a rep clicks and lands nowhere, and one
/// pointing at somebody else's payment is a disclosure — both refused at the edge rather than
/// discovered by whoever reads the note back six months later.
/// </para>
/// <para>
/// <b>Read-only, and pointedly so.</b> A payment moves through <c>Payment.Settle</c> inside
/// Payments, reached only by the provider's answer; a second module that could write to one is a
/// second module that owns it. The split is the one <see cref="IBillDirectory"/> already documents.
/// </para>
/// </remarks>
public interface IPaymentDirectory
{
    /// <summary>One payment, or <see langword="null"/> when there is no such id.</summary>
    Task<PaymentSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The payments among <paramref name="ids"/> that exist, keyed by id. Ids that match nothing are
    /// simply absent — a caller rendering a list has to cope with one it cannot resolve anyway.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PaymentSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every payment of <paramref name="customerId"/>'s, oldest first — attempts included (WP-2.14).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The whole history, not a window</b>, for the reason <c>IBillDirectory</c>'s equivalent
    /// gives: a statement's opening balance is what every earlier movement adds up to, and a call
    /// that quietly returned the recent ones would produce a document that proves out and is wrong.
    /// A caller handed exactly <paramref name="limit"/> rows has to assume the history did not fit.
    /// </para>
    /// <para>
    /// <b>Refusals come back too, and the caller decides what to do with them.</b> A statement
    /// credits only what settled — a declined card moved no money — while a payment-history export
    /// shows the attempt, because "why does this customer still owe money" is answered by the run of
    /// declines and not by the silence where they would be. <c>IsSettled</c> is how the two tell
    /// them apart, which is why this seam has never flattened it.
    /// </para>
    /// </remarks>
    /// <param name="customerId">Whose payments.</param>
    /// <param name="limit">Most payments to return.</param>
    Task<IReadOnlyList<PaymentSummary>> ForCustomerAsync(
        Guid customerId,
        int limit,
        CancellationToken cancellationToken = default);
}
