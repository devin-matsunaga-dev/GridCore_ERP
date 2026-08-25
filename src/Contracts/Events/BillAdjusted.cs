namespace GridCore.Contracts.Events;

/// <summary>
/// Billing corrected what an already-issued bill comes to. Finance consumes this to post the
/// correction against the receivable it raised on <see cref="BillIssued"/>; it never calls back
/// into Billing.
/// </summary>
/// <remarks>
/// <para>
/// <b>A correction, never a replacement.</b> Invariant 3 makes a ledger correction a new entry
/// rather than an edit, and this event is that habit reaching across the module boundary: it states
/// the <i>change</i> to what is owed, so Finance posts a second balanced journal entry rather than
/// going back and rewriting the first. The bill it names still says what it said.
/// </para>
/// <para>
/// <see cref="Amount"/> is signed the way the money moves — negative for a credit, positive for a
/// charge — so a consumer sums adjustments without a lookup table of which kinds count as which
/// direction. That is the call WP-1.4 made for <c>StockMovement.QuantityChange</c>, for the same
/// reason. <see cref="Kind"/> is there to be read, not to be arithmetic on.
/// </para>
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the bill was adjusted.</param>
/// <param name="BillId">The bill in Billing's schema.</param>
/// <param name="BillNumber">Human-readable bill number, as printed for the customer.</param>
/// <param name="ServiceAccountId">The service account billed.</param>
/// <param name="CustomerId">The customer who owes it.</param>
/// <param name="AdjustmentId">The adjustment entry this event states, for a consumer to trace back to.</param>
/// <param name="Kind">Whether it is a credit or a charge, by name.</param>
/// <param name="Amount">
/// The signed change to what is owed — negative for a credit. Money is <see langword="decimal"/>,
/// never a float.
/// </param>
/// <param name="AmountDue">What the bill comes to once this adjustment is applied.</param>
/// <param name="Currency">ISO 4217 code both amounts are expressed in.</param>
/// <param name="Reason">Why it was adjusted, in the billing officer's words.</param>
public sealed record BillAdjusted(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid BillId,
    string BillNumber,
    Guid ServiceAccountId,
    Guid CustomerId,
    Guid AdjustmentId,
    string Kind,
    decimal Amount,
    decimal AmountDue,
    string Currency,
    string Reason) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static BillAdjusted For(
        DateTimeOffset occurredAt,
        Guid billId,
        string billNumber,
        Guid serviceAccountId,
        Guid customerId,
        Guid adjustmentId,
        string kind,
        decimal amount,
        decimal amountDue,
        string currency,
        string reason) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            billId,
            billNumber,
            serviceAccountId,
            customerId,
            adjustmentId,
            kind,
            amount,
            amountDue,
            currency,
            reason);
}
