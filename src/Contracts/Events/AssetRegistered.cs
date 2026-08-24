namespace GridCore.Contracts.Events;

/// <summary>
/// A utility asset was entered in the register — a transformer, a pole, a span of conductor, a
/// vehicle. Published when the record is created, whatever state the asset is in: an asset sitting
/// in the yard is as real as one on a pole, and Inventory and Work Orders both want to know it
/// exists before anybody is sent to it.
/// </summary>
/// <remarks>
/// Nothing consumes this yet, for the same reason as <see cref="CustomerRegistered"/>: the fact is
/// worth recording the day it becomes true, and publishing through the outbox from the first write
/// keeps invariant 2 a habit. Work Orders (WP-3.1) is the first module that will want it — a job is
/// raised against an asset.
/// </remarks>
/// <param name="EventId">Identity of this event.</param>
/// <param name="OccurredAt">When the asset was registered.</param>
/// <param name="AssetId">The asset in the Assets schema.</param>
/// <param name="AssetTag">Human-readable asset tag, as stencilled on the plant.</param>
/// <param name="Class">What kind of asset it is, by name.</param>
/// <param name="Status">The status it was registered in, by name.</param>
/// <param name="Condition">The condition it was registered in, by name.</param>
public sealed record AssetRegistered(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid AssetId,
    string AssetTag,
    string Class,
    string Status,
    string Condition) : IIntegrationEvent
{
    /// <summary>Builds the event, stamping a Guid v7 identity from <paramref name="occurredAt"/>.</summary>
    public static AssetRegistered For(
        DateTimeOffset occurredAt,
        Guid assetId,
        string assetTag,
        string @class,
        string status,
        string condition) =>
        new(
            Guid.CreateVersion7(occurredAt),
            occurredAt,
            assetId,
            assetTag,
            @class,
            status,
            condition);
}
