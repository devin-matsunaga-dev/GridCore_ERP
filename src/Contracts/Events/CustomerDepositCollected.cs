namespace GridCore.Contracts.Events;

/// <summary>
/// A security deposit was taken from a customer and is now held against their account. Finance
/// consumes this to post the liability — debit cash, credit customer deposits — because money the
/// utility is holding on somebody else's behalf is owed back, not earned.
/// </summary>
/// <remarks>
/// <para>
/// Published by Customers, which owns the deposit ledger (WP-2.12). It names the entry rather than
/// the balance: a deposit is a series of immutable movements, and an event carrying "the balance is
/// now 75.00" would be a snapshot two redeliveries could disagree about, whereas "50.00 was taken,
/// entry <c>X</c>" stays true forever.
/// </para>
/// <para>
/// <see cref="IsInterestBearing"/> travels with the fact because it is a term of the holding, but
/// nothing accrues on it in the MVP — it is stored so a later package has the terms it was taken
/// under rather than having to guess them retrospectively.
/// </para>
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the deposit was taken.</param>
/// <param name="DepositEntryId">The ledger entry in the Customers schema.</param>
/// <param name="CustomerId">Who paid it.</param>
/// <param name="AccountNumber">The number they quote, so a posting reads as something a person recognises.</param>
/// <param name="Amount">How much was taken. Always positive; money is <see langword="decimal"/>, never a float.</param>
/// <param name="BalanceAfter">What the utility holds for this customer once this entry is applied.</param>
/// <param name="Currency">ISO 4217 code the amount is expressed in.</param>
/// <param name="IsInterestBearing">Whether the holding earns interest. Stored, never accrued in the MVP.</param>
public sealed record CustomerDepositCollected(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid DepositEntryId,
    Guid CustomerId,
    string AccountNumber,
    decimal Amount,
    decimal BalanceAfter,
    string Currency,
    bool IsInterestBearing) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static CustomerDepositCollected For(
        DateTimeOffset occurredAt,
        Guid depositEntryId,
        Guid customerId,
        string accountNumber,
        decimal amount,
        decimal balanceAfter,
        string currency,
        bool isInterestBearing) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            depositEntryId,
            customerId,
            accountNumber,
            amount,
            balanceAfter,
            currency,
            isInterestBearing);
}
