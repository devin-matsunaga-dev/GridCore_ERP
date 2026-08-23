namespace GridCore.Contracts.Events;

/// <summary>
/// The payment provider approved a customer payment. Finance consumes this to post the cash
/// receipt (debit cash, credit AR). Only approved payments are published — a declined attempt is
/// Payments' own business and never reaches the ledger.
/// </summary>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the provider approved the payment.</param>
/// <param name="PaymentId">The payment in Payments' schema.</param>
/// <param name="ServiceAccountId">The service account credited.</param>
/// <param name="CustomerId">The customer who paid.</param>
/// <param name="BillId">The bill settled, when the payment was made against one.</param>
/// <param name="Amount">Amount approved. Money is <see langword="decimal"/>, never a float.</param>
/// <param name="Currency">ISO 4217 code the amount is expressed in.</param>
/// <param name="Method">How it was paid, e.g. <c>card</c> or <c>bank-transfer</c>.</param>
/// <param name="ProviderReference">The provider's own reference, for reconciliation.</param>
public sealed record PaymentApproved(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PaymentId,
    Guid ServiceAccountId,
    Guid CustomerId,
    Guid? BillId,
    decimal Amount,
    string Currency,
    string Method,
    string ProviderReference) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static PaymentApproved For(
        DateTimeOffset occurredAt,
        Guid paymentId,
        Guid serviceAccountId,
        Guid customerId,
        Guid? billId,
        decimal amount,
        string currency,
        string method,
        string providerReference) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            paymentId,
            serviceAccountId,
            customerId,
            billId,
            amount,
            currency,
            method,
            providerReference);
}
