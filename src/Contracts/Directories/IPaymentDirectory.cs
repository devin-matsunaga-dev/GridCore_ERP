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
/// linking to a payment asks (WP-2.13). Nothing here is a balance and nothing here decides anything;
/// widening it is a work package, not a field.
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
/// <param name="RequestedAt">When it was taken.</param>
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
    DateTimeOffset RequestedAt);

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
}
