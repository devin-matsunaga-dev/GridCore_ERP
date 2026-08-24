namespace GridCore.Contracts.Events;

/// <summary>
/// An asset moved through its lifecycle — installed, taken out for maintenance, returned to stock,
/// or retired. One event for every move rather than one per verb, because the interesting fact for
/// a consumer is the pair: Work Orders cares that a transformer left service, Finance cares only
/// about the move to <c>Retired</c>, and both read it off the same message.
/// </summary>
/// <remarks>
/// Nothing consumes this yet (WP-1.1's precedent). An asset's <i>condition</i> deliberately has no
/// event: condition is an assessment the Assets module owns and revises, where status is the fact
/// other modules gate on.
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the status moved.</param>
/// <param name="AssetId">The asset in the Assets schema.</param>
/// <param name="AssetTag">Human-readable asset tag.</param>
/// <param name="FromStatus">Where the asset was, by name.</param>
/// <param name="ToStatus">Where it went, by name.</param>
/// <param name="Reason">Why, in the operator's words, where one was given.</param>
public sealed record AssetStatusChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid AssetId,
    string AssetTag,
    string FromStatus,
    string ToStatus,
    string? Reason) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static AssetStatusChanged For(
        DateTimeOffset occurredAt,
        Guid assetId,
        string assetTag,
        string fromStatus,
        string toStatus,
        string? reason) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            assetId,
            assetTag,
            fromStatus,
            toStatus,
            reason);
}
