namespace GridCore.Modules.Inventory.Features.Items;

/// <summary>
/// What kind of thing a stocked item is — the filter a storeman and a registry screen reach for
/// first.
/// </summary>
/// <remarks>
/// <para>
/// These are categories of <b>catalogue line</b>, not of device. <see cref="Transformer"/> here is
/// "the kind of thing a spare transformer is", a type of which the store holds a quantity; the
/// individual transformer standing on a pole is an <c>Asset</c> with a tag and a history, in the
/// Assets module. The same distinction is why <see cref="Metering"/> can exist here without
/// reopening WP-1.3's decision that there is no <c>Meter</c> asset class: a boxed meter on a shelf
/// is stock, and the meter registry that records the fitted device is Metering's (WP-2.1).
/// </para>
/// </remarks>
public enum StockItemCategory
{
    /// <summary>Conductor, cable and earth wire, held by the metre.</summary>
    Conductor = 1,

    /// <summary>Line hardware — connectors, clamps, insulators, arresters, poles.</summary>
    Hardware = 2,

    /// <summary>Distribution plant held as a spare until it is installed and becomes an asset.</summary>
    Transformer = 3,

    /// <summary>Meters and metering accessories, before they are fitted and registered.</summary>
    Metering = 4,

    /// <summary>Used up doing the job — oil, tape, compound, fixings.</summary>
    Consumable = 5,

    /// <summary>Tools a crew signs out.</summary>
    Tooling = 6,

    /// <summary>Personal protective equipment.</summary>
    Safety = 7,
}
