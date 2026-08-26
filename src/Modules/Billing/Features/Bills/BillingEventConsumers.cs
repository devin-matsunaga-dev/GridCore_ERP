using GridCore.Contracts.Events;
using GridCore.Platform.Messaging;

namespace GridCore.Modules.Billing.Features.Bills;

/// <summary>
/// What an approved payment means to a bill. Free of MassTransit types on purpose, so the whole
/// consume path can be exercised in the fast tier without a bus or a broker — the same split
/// <see cref="IdempotentEventHandler"/> is built around, with
/// <see cref="PaymentApprovedConsumer"/> as the thin adapter that connects it to the transport.
/// </summary>
public static class BillPayments
{
    /// <summary>
    /// Applies <paramref name="message"/> to the bill it names.
    /// </summary>
    /// <remarks>
    /// A payment taken against no particular bill is an account credit, which nothing raises yet —
    /// <see cref="PaymentApproved.BillId"/> is nullable because a later work package will. Ignored
    /// rather than faulted: a consumer that threw on a fact it has no work for would park the
    /// message on a dead-letter queue for no reason.
    /// </remarks>
    public static Task ApplyAsync(IBillService bills, PaymentApproved message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bills);
        ArgumentNullException.ThrowIfNull(message);

        if (message.BillId is not { } billId)
        {
            return Task.CompletedTask;
        }

        return bills.RecordPaymentAsync(
            billId,
            new RecordBillPaymentInput(
                message.Amount,
                message.PaymentId,
                message.ProviderReference,
                $"Payment {message.ProviderReference} ({message.Method}) approved."),
            cancellationToken);
    }
}

/// <summary>
/// Reduces what a bill is owed when Payments reports that money arrived.
/// </summary>
/// <remarks>
/// <para>
/// <b>Billing's first consumer.</b> Until now this module only published; the receivable it raised
/// on <c>BillIssued</c> is relieved here, by the one module that owns the document. Payments states
/// that money moved and never touches a bill — that split is what keeps the balance a single
/// module's business, and it is why <see cref="Bill.RecordPayment"/> was written in WP-2.3 and left
/// with no caller until now.
/// </para>
/// <para>
/// <b>Idempotency is not decoration here — it is the work package's requirement.</b> A broker
/// redelivers; without the claim <see cref="IdempotentConsumer{TEvent}"/> takes, a redelivered
/// approval would reduce the same balance twice and a customer would be shown as having paid money
/// they never sent. The handler and the claim commit together, so a redelivery after a failure
/// still gets a real second attempt.
/// </para>
/// <para>
/// A pure adapter: the transport, the transaction and the deduplication belong to the base class,
/// and what a payment means to a bill belongs to <see cref="BillPayments"/>. The same shape
/// Finance's consumers take.
/// </para>
/// </remarks>
public sealed class PaymentApprovedConsumer(IdempotentEventHandler handler, IBillService bills)
    : IdempotentConsumer<PaymentApproved>(handler)
{
    /// <summary>
    /// Stable dedupe identity. <b>Never rename</b>: a new name means every past payment looks
    /// unhandled, and the next redelivery would pay every bill in the register a second time.
    /// Distinct from <c>finance.payment-approved</c> because the two modules claim the same event
    /// independently — each has its own work to do with it, and a shared name would mean whichever
    /// handled it first silently suppressed the other.
    /// </summary>
    public const string Name = "billing.payment-approved";

    /// <inheritdoc />
    protected override string ConsumerName => Name;

    /// <inheritdoc />
    protected override Task ConsumeAsync(PaymentApproved message, CancellationToken cancellationToken) =>
        BillPayments.ApplyAsync(bills, message, cancellationToken);
}

/// <summary>
/// What an applied deposit means to a bill. Free of MassTransit types on purpose, the same split
/// <see cref="BillPayments"/> makes, so the whole consume path is exercised in the fast tier
/// without a bus or a broker.
/// </summary>
public static class BillDeposits
{
    /// <summary>Applies <paramref name="message"/> to the bill it names.</summary>
    /// <remarks>
    /// No "no bill" case to skip, unlike <see cref="BillPayments.ApplyAsync"/>: a deposit is applied
    /// <i>to a bill</i> or it is not applied at all — Customers refuses an application that names
    /// none — so <see cref="CustomerDepositApplied.BillId"/> is not nullable and there is nothing to
    /// ignore.
    /// </remarks>
    public static Task ApplyAsync(IBillService bills, CustomerDepositApplied message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bills);
        ArgumentNullException.ThrowIfNull(message);

        return bills.RecordDepositAsync(
            message.BillId,
            new RecordBillDepositInput(
                message.Amount,
                message.DepositEntryId,
                $"Security deposit of {message.Amount:0.00} applied."),
            cancellationToken);
    }
}

/// <summary>
/// Reduces what a bill is owed when Customers reports that a security deposit was put against it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The deposit never becomes an adjustment.</b> WP-2.4's rule is that money moving is not a
/// lifecycle state, and WORK_PACKAGES.md restates it for WP-2.12: applying a deposit "must leave the
/// bill's own adjustment trail alone". So this settles the bill the way an approved payment does —
/// against the amount paid — and the bill's corrections stay exactly as they were.
/// </para>
/// <para>
/// <b>Customers asks whether the bill can take it before it publishes</b>, through
/// <c>IBillDirectory</c>, so this normally succeeds. It can still fail if the balance moved in
/// between — a payment landing first, say — and that is deliberate: <see cref="Bill.RecordPayment"/>
/// refuses an overpayment, the message is retried and then dead-lettered, and a bill that quietly
/// swallowed the difference would leave money with no record of where it went.
/// </para>
/// </remarks>
public sealed class CustomerDepositAppliedConsumer(IdempotentEventHandler handler, IBillService bills)
    : IdempotentConsumer<CustomerDepositApplied>(handler)
{
    /// <summary>
    /// Stable dedupe identity. <b>Never rename</b>: a new name means every past application looks
    /// unhandled, and the next redelivery would settle every one of those bills a second time.
    /// Distinct from <c>finance.deposit-applied</c> because the two modules claim the same event
    /// independently — each has its own work to do with it, and a shared name would mean whichever
    /// handled it first silently suppressed the other.
    /// </summary>
    public const string Name = "billing.deposit-applied";

    /// <inheritdoc />
    protected override string ConsumerName => Name;

    /// <inheritdoc />
    protected override Task ConsumeAsync(CustomerDepositApplied message, CancellationToken cancellationToken) =>
        BillDeposits.ApplyAsync(bills, message, cancellationToken);
}
