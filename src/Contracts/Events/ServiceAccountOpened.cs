namespace GridCore.Contracts.Events;

/// <summary>
/// A service account was opened, joining a customer to a premise. Published when the account is
/// created — before service is energised, which is <see cref="ServiceStarted"/>.
/// </summary>
/// <remarks>
/// Nothing consumes this yet, for the same reason as <see cref="CustomerRegistered"/>: the fact is
/// worth recording the day it becomes true, and publishing through the outbox from the first write
/// keeps invariant 2 a habit. Metering (WP-2.1) is the first module that will want it — a meter is
/// fitted against an account, not against a customer.
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the account was opened.</param>
/// <param name="ServiceAccountId">The account in the Customers schema.</param>
/// <param name="AccountNumber">Human-readable account number, as quoted to the customer.</param>
/// <param name="CustomerId">Who is being served.</param>
/// <param name="ServiceLocationId">Where they are being served.</param>
/// <param name="Status">The status the account was opened in, by name.</param>
public sealed record ServiceAccountOpened(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    Guid ServiceLocationId,
    string Status) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static ServiceAccountOpened For(
        DateTimeOffset occurredAt,
        Guid serviceAccountId,
        string accountNumber,
        Guid customerId,
        Guid serviceLocationId,
        string status) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            serviceAccountId,
            accountNumber,
            customerId,
            serviceLocationId,
            status);
}
