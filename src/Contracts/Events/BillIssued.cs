namespace GridCore.Contracts.Events;

/// <summary>
/// Billing issued a bill against a service account. Finance consumes this to post the receivable
/// (debit AR, credit revenue); it never calls back into Billing.
/// </summary>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the bill was issued.</param>
/// <param name="BillId">The bill in Billing's schema.</param>
/// <param name="BillNumber">Human-readable bill number, as printed for the customer.</param>
/// <param name="ServiceAccountId">The service account billed.</param>
/// <param name="CustomerId">The customer who owes it.</param>
/// <param name="PeriodStart">First day of the billed period.</param>
/// <param name="PeriodEnd">Last day of the billed period.</param>
/// <param name="DueDate">When payment falls due.</param>
/// <param name="Amount">Total billed. Money is <see langword="decimal"/>, never a float.</param>
/// <param name="Currency">ISO 4217 code the amount is expressed in.</param>
public sealed record BillIssued(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid BillId,
    string BillNumber,
    Guid ServiceAccountId,
    Guid CustomerId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly DueDate,
    decimal Amount,
    string Currency) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static BillIssued For(
        DateTimeOffset occurredAt,
        Guid billId,
        string billNumber,
        Guid serviceAccountId,
        Guid customerId,
        DateOnly periodStart,
        DateOnly periodEnd,
        DateOnly dueDate,
        decimal amount,
        string currency) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            billId,
            billNumber,
            serviceAccountId,
            customerId,
            periodStart,
            periodEnd,
            dueDate,
            amount,
            currency);
}
