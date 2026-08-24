namespace GridCore.Contracts.Events;

/// <summary>
/// A meter was fitted at a premise and is now what measures supply there. The fact billing is built
/// on: from this moment a reading taken off this meter is consumption at that service location.
/// </summary>
/// <remarks>
/// <para>
/// Carries the premise, never a service account. A meter is fitted to a <i>place</i> — the pipe or
/// the service drop is at the premise, and the premise outlives whoever is being served there. A
/// consumer that needs the account resolves it from the premise through the Customers module,
/// which is the same derivation every screen makes.
/// </para>
/// <para>
/// Nothing consumes this yet. WP-2.2's readings and WP-2.3's bills are its intended audience.
/// </para>
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the meter was fitted.</param>
/// <param name="MeterId">The meter in the Metering schema.</param>
/// <param name="MeterNumber">Human-readable meter number.</param>
/// <param name="MeterType">What kind of meter it is, by name.</param>
/// <param name="ServiceLocationId">The premise it now measures, in the Customers schema.</param>
public sealed record MeterInstalled(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid MeterId,
    string MeterNumber,
    string MeterType,
    Guid ServiceLocationId) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static MeterInstalled For(
        DateTimeOffset occurredAt,
        Guid meterId,
        string meterNumber,
        string meterType,
        Guid serviceLocationId) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            meterId,
            meterNumber,
            meterType,
            serviceLocationId);
}
