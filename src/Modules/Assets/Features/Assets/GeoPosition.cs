using System.Globalization;
using GridCore.Modules.Assets.Features.Shared;

namespace GridCore.Modules.Assets.Features.Assets;

/// <summary>
/// Where a piece of plant physically stands, as a WGS 84 latitude and longitude. A value, not an
/// entity: a coordinate has no identity apart from the thing standing at it.
/// </summary>
/// <remarks>
/// <para>
/// The point of the type is <b>both or neither</b>. Two loose nullable columns let an asset be
/// saved with a latitude and no longitude, which is not a location at all — it is a crew driving
/// somewhere along a line of latitude. Constructing one is the only way to set an asset's position,
/// so a half-coordinate cannot be expressed.
/// </para>
/// <para>
/// <see langword="decimal"/> rather than <see langword="double"/>, stored as <c>numeric(9,6)</c>:
/// six decimal places is about 11 cm, far finer than a pole needs, and the stored value is exactly
/// the one the surveyor read rather than the nearest binary fraction to it.
/// </para>
/// </remarks>
/// <param name="Latitude">Degrees north of the equator, −90 to 90.</param>
/// <param name="Longitude">Degrees east of Greenwich, −180 to 180.</param>
public readonly record struct GeoPosition(decimal Latitude, decimal Longitude)
{
    /// <summary>Decimal places stored — about 11 cm at the equator.</summary>
    public const int DecimalPlaces = 6;

    /// <summary>Total digits stored: three for the degrees, <see cref="DecimalPlaces"/> after the point.</summary>
    public const int Precision = 9;

    /// <summary>Builds a position, refusing one that is not on the planet.</summary>
    /// <exception cref="AssetValidationException">Either value is out of range, or finer than the column stores.</exception>
    public static GeoPosition Create(decimal latitude, decimal longitude) =>
        new(
            Degrees(latitude, 90m, nameof(latitude)),
            Degrees(longitude, 180m, nameof(longitude)));

    /// <summary>
    /// A position from an optional pair — <see langword="null"/> when neither is given, and a
    /// refusal when only one is. The seam between an API body's two nullable fields and the value.
    /// </summary>
    /// <exception cref="AssetValidationException">Exactly one of the two was supplied.</exception>
    public static GeoPosition? From(decimal? latitude, decimal? longitude) => (latitude, longitude) switch
    {
        (null, null) => null,
        ({ } lat, { } lon) => Create(lat, lon),
        _ => throw new AssetValidationException(
            "A position needs both 'latitude' and 'longitude'; one on its own is a line, not a place."),
    };

    /// <summary>The pair as an operator would write it, e.g. <c>14.141389, 145.187778</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Latitude}, {Longitude}");

    private static decimal Degrees(decimal value, decimal limit, string field)
    {
        if (decimal.Abs(value) > limit)
        {
            throw new AssetValidationException($"'{field}' must be between -{limit} and {limit}; '{value}' is not.");
        }

        // Refused rather than rounded, for the same reason a deposit finer than a cent is (WP-1.1):
        // CONVENTIONS.md's central rounding helper has no home yet, and numeric(9,6) would have
        // truncated silently to a position nobody surveyed.
        if (decimal.Round(value, DecimalPlaces) != value)
        {
            throw new AssetValidationException(
                $"'{field}' is stored to {DecimalPlaces} decimal places; '{value}' is finer than that.");
        }

        return value;
    }
}
