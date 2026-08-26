namespace GridCore.Contracts.Events;

/// <summary>
/// A customer's security deposit was put against a bill they owe. <b>Two modules consume this, and
/// each has its own work to do with it:</b> Finance posts the transfer — debit customer deposits,
/// credit accounts receivable — and Billing reduces what the bill is owed.
/// </summary>
/// <remarks>
/// <para>
/// The money does not enter the utility here; it was already held. What changes is what it is held
/// <i>for</i>: a liability the utility owed back becomes a receivable it no longer has to collect.
/// That is why no cash line appears on either side of the posting.
/// </para>
/// <para>
/// <b>A payment-side effect, never a bill adjustment.</b> WP-2.4's rule holds — money moving is not
/// a lifecycle state, and a bill settled from a deposit keeps its own adjustment trail untouched.
/// Billing therefore records this the way it records an approved payment, against the amount paid.
/// </para>
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the deposit was applied.</param>
/// <param name="DepositEntryId">The ledger entry in the Customers schema.</param>
/// <param name="CustomerId">Whose deposit it was.</param>
/// <param name="ServiceAccountId">The service account the bill was raised against.</param>
/// <param name="BillId">The bill settled, in Billing's schema.</param>
/// <param name="BillNumber">The number printed on it, so a posting reads as something a person recognises.</param>
/// <param name="Amount">How much was applied. Always positive; money is <see langword="decimal"/>, never a float.</param>
/// <param name="BalanceAfter">What the utility still holds for this customer once this entry is applied.</param>
/// <param name="Currency">ISO 4217 code the amount is expressed in.</param>
public sealed record CustomerDepositApplied(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid DepositEntryId,
    Guid CustomerId,
    Guid ServiceAccountId,
    Guid BillId,
    string BillNumber,
    decimal Amount,
    decimal BalanceAfter,
    string Currency) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static CustomerDepositApplied For(
        DateTimeOffset occurredAt,
        Guid depositEntryId,
        Guid customerId,
        Guid serviceAccountId,
        Guid billId,
        string billNumber,
        decimal amount,
        decimal balanceAfter,
        string currency) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            depositEntryId,
            customerId,
            serviceAccountId,
            billId,
            billNumber,
            amount,
            balanceAfter,
            currency);
}
