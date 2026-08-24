namespace GridCore.Contracts.Events;

/// <summary>
/// Service was cut on an account — the premise is disconnected but the account is still open, so it
/// can be reconnected without a new registration.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ServiceAccountClosed"/> on purpose: a disconnection stops consumption
/// but leaves a balance to settle and a premise still allocated, where a closure is final.
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When service stopped.</param>
/// <param name="ServiceAccountId">The account that was disconnected.</param>
/// <param name="AccountNumber">Human-readable account number.</param>
/// <param name="CustomerId">Who was being served.</param>
/// <param name="ServiceLocationId">Where they were being served.</param>
/// <param name="Reason">Why service was stopped, where one was recorded.</param>
public sealed record ServiceStopped(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    Guid ServiceLocationId,
    string? Reason) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static ServiceStopped For(
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
