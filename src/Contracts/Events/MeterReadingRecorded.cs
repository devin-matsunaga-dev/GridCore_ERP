namespace GridCore.Contracts.Events;

/// <summary>
/// A reading was taken off a meter and what it consumed since the last one is now known. The fact
/// the revenue cycle starts from: SPEC.md's workflow runs generate reading → calculate consumption
/// → generate bill, and this is the first two of those becoming true.
/// </summary>
/// <remarks>
/// <para>
/// Carries the premise, never a service account — the same call as <see cref="MeterInstalled"/> and
/// for the same reason. A consumer that needs the account resolves "the account open at this
/// premise" through the Customers module, which is the derivation every screen already makes.
/// </para>
/// <para>
/// Published for a missing read too, with a null <see cref="Reading"/> and null
/// <see cref="Consumption"/>. A cycle that could not read a meter is a fact billing has to know:
/// the alternative is a bill quietly raised on stale dials.
/// </para>
/// <para>
/// Nothing consumes this yet. WP-2.3's bill generation is its intended audience.
/// </para>
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the reading was recorded.</param>
/// <param name="ReadingId">The reading in the Metering schema.</param>
/// <param name="MeterId">The meter it came off.</param>
/// <param name="MeterNumber">Human-readable meter number.</param>
/// <param name="ServiceLocationId">The premise the meter was measuring, in the Customers schema.</param>
/// <param name="ReadingDate">The date the dials were read.</param>
/// <param name="Reading">What they read, or <see langword="null"/> for a missing read.</param>
/// <param name="Consumption">
/// Units used since the previous reading, or <see langword="null"/> when there is nothing to
/// measure from — a missing read, or the first reading on a meter fitted without one.
/// </param>
/// <param name="ExceptionCode">The reading exception raised, by name; <c>None</c> when the read was ordinary.</param>
/// <param name="CycleCode">The reading cycle it belongs to, or <see langword="null"/> for a manual read.</param>
public sealed record MeterReadingRecorded(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ReadingId,
    Guid MeterId,
    string MeterNumber,
    Guid ServiceLocationId,
    DateTimeOffset ReadingDate,
    decimal? Reading,
    decimal? Consumption,
    string ExceptionCode,
    string? CycleCode) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static MeterReadingRecorded For(
        DateTimeOffset occurredAt,
        Guid readingId,
        Guid meterId,
        string meterNumber,
        Guid serviceLocationId,
        DateTimeOffset readingDate,
        decimal? reading,
        decimal? consumption,
        string exceptionCode,
        string? cycleCode) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            readingId,
            meterId,
            meterNumber,
            serviceLocationId,
            readingDate,
            reading,
            consumption,
            exceptionCode,
            cycleCode);
}
