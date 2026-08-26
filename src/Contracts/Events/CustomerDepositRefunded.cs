namespace GridCore.Contracts.Events;

/// <summary>
/// A security deposit was given back to the customer. Finance consumes this to reverse the
/// liability — debit customer deposits, credit cash — the exact opposite of taking it.
/// </summary>
/// <remarks>
/// <b>A refund is a new entry, never an unwinding of the collection.</b> Invariant 3 makes a
/// correction another entry rather than an edit, and the same rule governs the deposit ledger the
/// posting comes from: the collection stays exactly as it was recorded, and this sits beside it. A
/// customer who paid a deposit and later had it returned has two movements in their history, which
/// is what happened.
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the deposit was refunded.</param>
/// <param name="DepositEntryId">The ledger entry in the Customers schema.</param>
/// <param name="CustomerId">Who it went back to.</param>
/// <param name="AccountNumber">The number they quote, so a posting reads as something a person recognises.</param>
/// <param name="Amount">How much was returned. Always positive; money is <see langword="decimal"/>, never a float.</param>
/// <param name="BalanceAfter">What the utility still holds for this customer once this entry is applied.</param>
/// <param name="Currency">ISO 4217 code the amount is expressed in.</param>
/// <param name="Reason">Why it was returned, in the operator's words.</param>
public sealed record CustomerDepositRefunded(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid DepositEntryId,
    Guid CustomerId,
    string AccountNumber,
    decimal Amount,
    decimal BalanceAfter,
    string Currency,
    string? Reason) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static CustomerDepositRefunded For(
        DateTimeOffset occurredAt,
        Guid depositEntryId,
        Guid customerId,
        string accountNumber,
        decimal amount,
        decimal balanceAfter,
        string currency,
        string? reason) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            depositEntryId,
            customerId,
            accountNumber,
            amount,
            balanceAfter,
            currency,
            reason);
}
