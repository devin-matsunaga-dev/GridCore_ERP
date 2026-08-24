namespace GridCore.Contracts.Events;

/// <summary>
/// Service was energised on an account — the premise is now connected and consuming. Published on
/// the first start and on every reconnection after a disconnection.
/// </summary>
/// <remarks>
/// This is the fact Metering and Billing hang their work off: a meter starts producing readings for
/// a connected premise, and a billing cycle only bills an account that was live during it.
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When service started.</param>
/// <param name="ServiceAccountId">The account that went live.</param>
/// <param name="AccountNumber">Human-readable account number.</param>
/// <param name="CustomerId">Who is being served.</param>
/// <param name="ServiceLocationId">Where they are being served.</param>
/// <param name="Reason">Why service was started, where one was recorded.</param>
public sealed record ServiceStarted(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    Guid ServiceLocationId,
    string? Reason) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static ServiceStarted For(
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
