namespace GridCore.Modules.Inventory.Features.Warehouses;

/// <summary>
/// The warehouses the utility ships with: reference data, not demo data. A migrated database can
/// receive and issue stock (ARCHITECTURE.md invariant 8) with no seeder involved.
/// </summary>
/// <remarks>
/// Three, because the Ops &amp; Maintenance cycle needs somewhere parts come <i>from</i> that is not
/// where they were bought <i>into</i> — a main store, a depot the crews draw from, and a yard for
/// bulk plant. Adding one is a new migration; migrations are append-only (invariant 7).
/// </remarks>
public static class DefaultWarehouses
{
    /// <summary>Code of the central store goods are received into.</summary>
    public const string MainStore = "MAIN";

    /// <summary>Code of the depot field crews draw parts from.</summary>
    public const string NorthDepot = "NORTH";

    /// <summary>Code of the yard holding bulk plant.</summary>
    public const string SubstationYard = "YARD";

    /// <summary>
    /// The instant this reference set was authored, and the timestamp component of every warehouse
    /// id. Fixed forever: changing it changes every id, which to the database is a different set.
    /// </summary>
    public static readonly DateTimeOffset AuthoredAt = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Every warehouse, in code order.</summary>
    public static IReadOnlyList<Warehouse> All { get; } =
    [
        Warehouse.Reference(MainStore, "Main store", "1 Utility Way, Central depot"),
        Warehouse.Reference(NorthDepot, "North depot", "45 Kestrel Road, North district"),
        Warehouse.Reference(SubstationYard, "Substation yard", "Substation 7, East industrial park"),
    ];

    /// <summary>The warehouse with <paramref name="code"/>.</summary>
    /// <exception cref="KeyNotFoundException">No warehouse has that code.</exception>
    public static Warehouse Require(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        return All.SingleOrDefault(warehouse => string.Equals(warehouse.Code, code, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"'{code}' is not a warehouse GridCore ships. Warehouses are reference data; adding one is a migration.");
    }
}
