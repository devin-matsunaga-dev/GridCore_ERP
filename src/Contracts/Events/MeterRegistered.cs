namespace GridCore.Contracts.Events;

/// <summary>
/// A meter was entered in the meter register. Published the day the fact becomes true, following
/// the precedent every GridCore registry has set, so a later consumer starts receiving it with no
/// change in Metering.
/// </summary>
/// <remarks>
/// Nothing consumes this yet (owner's call, as with <see cref="CustomerRegistered"/> and
/// <see cref="AssetRegistered"/>). A newly registered meter is stock sitting in a store; the facts
/// another module acts on are <see cref="MeterInstalled"/> and <see cref="MeterRemoved"/>, which
/// say what is metering a premise.
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the meter was registered.</param>
/// <param name="MeterId">The meter in the Metering schema.</param>
/// <param name="MeterNumber">Human-readable meter number.</param>
/// <param name="SerialNumber">The manufacturer's serial number stamped on the meter.</param>
/// <param name="MeterType">What kind of meter it is, by name.</param>
/// <param name="Status">Where it starts, by name.</param>
public sealed record MeterRegistered(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid MeterId,
    string MeterNumber,
    string SerialNumber,
    string MeterType,
    string Status) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static MeterRegistered For(
        DateTimeOffset occurredAt,
        Guid meterId,
        string meterNumber,
        string serialNumber,
        string meterType,
        string status) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            meterId,
            meterNumber,
            serialNumber,
            meterType,
            status);
}
