using GridCore.Contracts.Events;
using GridCore.Platform.Messaging;

namespace GridCore.Modules.Customers.Features.Arrangements;

/// <summary>
/// What an approved payment means to a payment arrangement. Free of MassTransit types on purpose, so
/// the whole consume path is exercised in the fast tier without a bus or a broker — the same split
/// Billing's <c>BillPayments</c> and Finance's consumers make, with
/// <see cref="ArrangementPaymentApprovedConsumer"/> as the thin adapter onto the transport.
/// </summary>
public static class ArrangementPayments
{
    /// <summary>
    /// Applies <paramref name="message"/> to the arrangement standing against the account it credits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Any approved payment on the account counts, not only one taken against an arranged bill.</b>
    /// An arrangement is a promise about the arrears as a whole; a customer who rings up and pays
    /// $120 has kept this month's instalment whichever bill the receipt happens to name. The service
    /// decides what to do with it — and answers null where no arrangement is in force, which is most
    /// payments.
    /// </para>
    /// <para>
    /// <b>It settles instalments and touches no bill.</b> Billing's own consumer of this same event
    /// reduces what the bill is owed; this one records that a promise was kept. The two claim the
    /// event under different consumer names precisely so neither suppresses the other.
    /// </para>
    /// </remarks>
    public static Task ApplyAsync(
        IPaymentArrangementService arrangements,
        PaymentApproved message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arrangements);
        ArgumentNullException.ThrowIfNull(message);

        return arrangements.RecordPaymentAsync(
            message.ServiceAccountId,
            message.Amount,
            message.PaymentId,
            message.ProviderReference,
            cancellationToken);
    }
}

/// <summary>
/// Settles an arrangement's instalments when Payments reports that money arrived (WP-2.20).
/// </summary>
/// <remarks>
/// <para>
/// <b>Customers' first consumer.</b> Until now this module only published — <c>CustomerRegistered</c>,
/// <c>ServiceAccountOpened</c>, <c>CustomerDepositApplied</c> and the rest. WORK_PACKAGES.md asks
/// that each instalment be "settled by a real payment through WP-2.5", and a real payment is a fact
/// Payments states rather than a figure a rep types into an arrangements screen.
/// </para>
/// <para>
/// <b>Settlement is therefore eventually consistent, and deliberately so.</b> The instalment is
/// marked paid a moment after the payment is approved rather than inside the same request — which is
/// the price of the module boundary, and cheap: nothing is decided on an instalment in the interval
/// except the account's protection, and protection can only improve when a payment lands.
/// </para>
/// <para>
/// <b>Idempotency is the requirement, not decoration.</b> A broker redelivers; without the claim
/// <see cref="IdempotentConsumer{TEvent}"/> takes, a redelivered approval would settle the schedule
/// twice and a customer would be shown as having kept instalments they never paid.
/// </para>
/// </remarks>
public sealed class ArrangementPaymentApprovedConsumer(
    IdempotentEventHandler handler,
    IPaymentArrangementService arrangements)
    : IdempotentConsumer<PaymentApproved>(handler)
{
    /// <summary>
    /// Stable dedupe identity. <b>Never rename</b>: a new name means every past payment looks
    /// unhandled, and the next redelivery would settle every schedule in the register a second time.
    /// Distinct from <c>billing.payment-approved</c> and <c>finance.payment-approved</c> because all
    /// three modules claim this event independently — each has its own work to do with it, and a
    /// shared name would mean whichever handled it first silently suppressed the others.
    /// </summary>
    public const string Name = "customers.payment-approved";

    /// <inheritdoc />
    protected override string ConsumerName => Name;

    /// <inheritdoc />
    protected override Task ConsumeAsync(PaymentApproved message, CancellationToken cancellationToken) =>
        ArrangementPayments.ApplyAsync(arrangements, message, cancellationToken);
}
