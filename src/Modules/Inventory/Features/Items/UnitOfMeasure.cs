namespace GridCore.Modules.Inventory.Features.Items;

/// <summary>
/// What one unit of a stocked item is. Deliberately short: a store that can measure things by the
/// each, the metre, the kilogram and the litre can hold everything a distribution utility issues to
/// a crew, and every extra unit is another conversion nobody asked for.
/// </summary>
public enum UnitOfMeasure
{
    /// <summary>Counted items — connectors, meters, transformers, gloves.</summary>
    Each = 1,

    /// <summary>Cut to length — conductor, cable, earth wire.</summary>
    Metre = 2,

    /// <summary>Weighed — bolts by the kilo, compound.</summary>
    Kilogram = 3,

    /// <summary>Poured — transformer oil, fuel.</summary>
    Litre = 4,
}

/// <summary>
/// What GridCore knows about a <see cref="UnitOfMeasure"/>. Kept out of the enum so a rule about
/// units lives beside the other units rather than in whichever aggregate happened to need it first.
/// </summary>
public static class UnitsOfMeasure
{
    /// <summary>
    /// Whether a fraction of one unit is a real quantity. Half a metre of conductor is; half a
    /// connector is a broken connector, and a store that admits one has a count nobody can trust.
    /// </summary>
    public static bool IsDivisible(UnitOfMeasure unit) => unit is not UnitOfMeasure.Each;
}
