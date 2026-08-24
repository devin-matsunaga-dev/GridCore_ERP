namespace GridCore.Modules.Assets.Features.Assets;

/// <summary>
/// What kind of plant an asset is. Deliberately the classes a distribution utility maintains and
/// sends crews to, not a taxonomy of everything it owns.
/// </summary>
/// <remarks>
/// There is no <c>Meter</c> here on purpose. A meter is registered, assigned and read through the
/// Metering module's own registry (WP-2.1); listing it as an asset class as well would give the
/// utility two records of one device, and the first bill dispute would find them disagreeing.
/// </remarks>
public enum AssetClass
{
    /// <summary>A distribution or pole-mounted transformer.</summary>
    Transformer = 1,

    /// <summary>A distribution pole carrying conductor, plant or a service drop.</summary>
    Pole = 2,

    /// <summary>A run of overhead or underground conductor between two structures.</summary>
    ConductorSpan = 3,

    /// <summary>A switch, sectionaliser or fuse cabinet.</summary>
    Switchgear = 4,

    /// <summary>A substation — the site and its common plant.</summary>
    Substation = 5,

    /// <summary>A generating set, including standby plant.</summary>
    Generator = 6,

    /// <summary>An automatic circuit recloser.</summary>
    Recloser = 7,

    /// <summary>A vehicle: bucket truck, digger derrick, service van.</summary>
    Vehicle = 8,
}
