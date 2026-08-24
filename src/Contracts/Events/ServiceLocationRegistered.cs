namespace GridCore.Contracts.Events;

/// <summary>
/// Customers registered a service location — a premise that can be metered, served and worked on.
/// Metering, Work Orders and Assets all attach to a location rather than to a customer, so the fact
/// that one exists is theirs to react to.
/// </summary>
/// <remarks>
/// As with <see cref="CustomerRegistered"/>, nothing consumes this yet; see that event's remarks.
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the location was registered.</param>
/// <param name="ServiceLocationId">The location in the Customers schema.</param>
/// <param name="LocationCode">Human-readable location code, as quoted on a work order.</param>
/// <param name="Address">The premise address on one line, for display.</param>
/// <param name="City">Town or village the premise is in.</param>
/// <param name="Region">State, province or island the premise is on.</param>
public sealed record ServiceLocationRegistered(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ServiceLocationId,
    string LocationCode,
    string Address,
    string City,
    string Region) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static ServiceLocationRegistered For(
        DateTimeOffset occurredAt,
        Guid serviceLocationId,
        string locationCode,
        string address,
        string city,
        string region) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            serviceLocationId,
            locationCode,
            address,
            city,
            region);
}
