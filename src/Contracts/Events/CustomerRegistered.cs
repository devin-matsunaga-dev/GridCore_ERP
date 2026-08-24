namespace GridCore.Contracts.Events;

/// <summary>
/// Customers registered a new customer. Published for the modules that will hang work off a
/// customer — service accounts, billing, payments — so none of them has to poll the registry.
/// </summary>
/// <remarks>
/// Nothing consumes this yet. It is published from WP-1.1 because the fact is worth recording the
/// day it becomes true: a consumer added in a later WP starts receiving it with no change here, and
/// publishing through the outbox from the first write is what keeps invariant 2 a habit rather than
/// something retrofitted once a consumer finally needs it.
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the customer was registered.</param>
/// <param name="CustomerId">The customer in the Customers schema.</param>
/// <param name="AccountNumber">Human-readable account number, as quoted to the customer.</param>
/// <param name="Name">Who they are — a person or an organisation.</param>
/// <param name="CustomerClass">Residential or commercial, by name.</param>
/// <param name="Status">The status the customer was registered in, by name.</param>
public sealed record CustomerRegistered(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid CustomerId,
    string AccountNumber,
    string Name,
    string CustomerClass,
    string Status) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static CustomerRegistered For(
        DateTimeOffset occurredAt,
        Guid customerId,
        string accountNumber,
        string name,
        string customerClass,
        string status) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            customerId,
            accountNumber,
            name,
            customerClass,
            status);
}
