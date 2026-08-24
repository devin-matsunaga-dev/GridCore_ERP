namespace GridCore.Contracts.Events;

/// <summary>
/// A meter was taken off a premise. Separate from <see cref="MeterInstalled"/> rather than one
/// "assignment changed" event, for the reason <c>ServiceStopped</c> is separate from
/// <c>ServiceAccountClosed</c>: the two facts have different consequences. An installation starts
/// measuring supply; a removal ends a metered period and leaves the premise unmetered until the
/// next meter is fitted, which is exactly the gap a billing run must not silently bridge.
/// </summary>
/// <remarks>Nothing consumes this yet; WP-2.2 and WP-2.3 are its intended audience.</remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the meter came off.</param>
/// <param name="MeterId">The meter in the Metering schema.</param>
/// <param name="MeterNumber">Human-readable meter number.</param>
/// <param name="ServiceLocationId">The premise it was measuring, in the Customers schema.</param>
/// <param name="Reason">Why it came off, in the operator's words, where one was given.</param>
public sealed record MeterRemoved(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid MeterId,
    string MeterNumber,
    Guid ServiceLocationId,
    string? Reason) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static MeterRemoved For(
        DateTimeOffset occurredAt,
        Guid meterId,
        string meterNumber,
        Guid serviceLocationId,
        string? reason) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            meterId,
            meterNumber,
            serviceLocationId,
            reason);
}
