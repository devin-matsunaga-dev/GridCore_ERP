using GridCore.Modules.Assets.Data;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Assets.Features.Shared;

/// <summary>
/// The prefix this module's registry numbers are issued under. The <i>shape</i> of a number is the
/// platform's (<see cref="RegistryNumbers"/>); what letters an asset tag carries is the Assets
/// module's own business.
/// </summary>
public static class AssetNumbers
{
    /// <summary>
    /// Prefix of an asset tag, e.g. <c>AST-000001</c>. Three letters rather than one: a tag is
    /// stencilled on plant and read off it in the field, and <c>A-000001</c> is already a service
    /// account number — a technician quoting the wrong one down a radio is a real failure mode.
    /// </summary>
    public const string AssetTagPrefix = "AST-";
}

/// <summary>
/// Issues the next asset tag. A seam, so the numbering scheme is one registration away from
/// changing — a utility migrating from a legacy register usually has to keep its own.
/// </summary>
public interface IAssetNumberGenerator
{
    /// <summary>The next unused asset tag.</summary>
    Task<string> NextAssetTagAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Continues the tag series from the highest tag already issued, inside the caller's transaction.
/// </summary>
/// <remarks>
/// One <see cref="RegistryNumberSeries.NextAsync"/> over this module's own column; the race with a
/// concurrent registration and the ordering trade it depends on are documented there, because every
/// registry shares them.
/// </remarks>
public sealed class SequentialAssetNumberGenerator(AssetsDbContext database) : IAssetNumberGenerator
{
    /// <inheritdoc />
    public Task<string> NextAssetTagAsync(CancellationToken cancellationToken = default) =>
        RegistryNumberSeries.NextAsync(
            AssetNumbers.AssetTagPrefix,
            database.Assets
                .Where(asset => asset.AssetTag.StartsWith(AssetNumbers.AssetTagPrefix))
                .OrderByDescending(asset => asset.AssetTag)
                .Select(asset => asset.AssetTag),
            cancellationToken);
}
