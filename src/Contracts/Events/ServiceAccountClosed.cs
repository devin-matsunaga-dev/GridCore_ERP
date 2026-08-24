namespace GridCore.Contracts.Events;

/// <summary>
/// A service account was closed for good. The premise it held is free for another account, and
/// nothing new attaches to this one — a returning customer gets a new account, not this one back.
/// </summary>
/// <remarks>
/// Billing's final-bill run and Metering's meter removal are the consumers this exists for.
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the account was closed.</param>
/// <param name="ServiceAccountId">The account that was closed.</param>
/// <param name="AccountNumber">Human-readable account number.</param>
/// <param name="CustomerId">Who was being served.</param>
/// <param name="ServiceLocationId">The premise the account is releasing.</param>
/// <param name="Reason">Why the account was closed, where one was recorded.</param>
public sealed record ServiceAccountClosed(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    Guid ServiceLocationId,
    string? Reason) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static ServiceAccountClosed For(
        DateTimeOffset occurredAt,
        Guid serviceAccountId,
        string accountNumber,
        Guid customerId,
        Guid serviceLocationId,
        string? reason = null) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            serviceAccountId,
            accountNumber,
            customerId,
            serviceLocationId,
            reason);
}
