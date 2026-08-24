namespace GridCore.Modules.Assets.Features.Assets;

/// <summary>
/// Where an asset stands in its working life. This is the <i>asset's</i> status — whether this
/// piece of plant is doing its job — and it is separate from <see cref="AssetCondition"/>, which is
/// how good a state it is in. A transformer can be <see cref="InService"/> and
/// <see cref="AssetCondition.Poor"/> at the same time; that pair is exactly what a maintenance plan
/// is built from.
/// </summary>
public enum AssetStatus
{
    /// <summary>Held in a warehouse or yard, not installed. Where a newly received asset starts.</summary>
    InStorage = 1,

    /// <summary>Installed and doing its job.</summary>
    InService = 2,

    /// <summary>Withdrawn from service for work, and expected back. Still the utility's plant, still on the register.</summary>
    UnderMaintenance = 3,

    /// <summary>
    /// Finished — scrapped, sold or written off. Terminal: the record stays readable because a work
    /// order, a cost and an inspection all point at it, and a returning refurbished unit is
    /// registered as the asset it now is rather than resurrecting this one.
    /// </summary>
    Retired = 4,
}

/// <summary>
/// The asset state machine, in one place. Kept out of <see cref="Asset"/> so a UI can ask what is
/// legal without holding an entity, matching <c>CustomerTransitions</c> and
/// <c>ServiceAccountTransitions</c>.
/// </summary>
public static class AssetTransitions
{
    private static readonly Dictionary<AssetStatus, AssetStatus[]> Allowed = new()
    {
        // No InStorage -> UnderMaintenance: maintenance is work on plant that was doing a job and
        // has been withdrawn to be fixed. Refurbishing something that never left the yard is stock
        // work, and calling it maintenance would put a job on an asset's service record that no
        // outage, no customer and no feeder ever saw.
        [AssetStatus.InStorage] = [AssetStatus.InService, AssetStatus.Retired],

        // Back to storage is the ordinary path for a unit recovered intact — a pole-top transformer
        // taken down on a rebuild goes back on the shelf, not to the scrap pile.
        [AssetStatus.InService] = [AssetStatus.UnderMaintenance, AssetStatus.InStorage, AssetStatus.Retired],

        [AssetStatus.UnderMaintenance] = [AssetStatus.InService, AssetStatus.InStorage, AssetStatus.Retired],

        [AssetStatus.Retired] = [],
    };

    /// <summary>Where a new asset starts, unless the caller says otherwise.</summary>
    public const AssetStatus Initial = AssetStatus.InStorage;

    /// <summary>The statuses an asset in <paramref name="status"/> may move to.</summary>
    public static IReadOnlyList<AssetStatus> AllowedFrom(AssetStatus status) =>
        Allowed.TryGetValue(status, out var next) ? next : [];

    /// <summary>Whether <paramref name="from"/> → <paramref name="to"/> is a legal move.</summary>
    public static bool IsAllowed(AssetStatus from, AssetStatus to) =>
        AllowedFrom(from).Contains(to);

    /// <summary>
    /// Whether an asset in <paramref name="status"/> is still part of the network the utility
    /// operates and maintains. A retired one is not, which is what keeps it out of a maintenance
    /// plan without deleting the history hanging off it.
    /// </summary>
    public static bool IsOnTheBooks(AssetStatus status) => status is not AssetStatus.Retired;
}
