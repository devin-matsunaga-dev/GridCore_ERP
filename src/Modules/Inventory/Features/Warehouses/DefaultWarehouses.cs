namespace GridCore.Modules.Inventory.Features.Warehouses;

/// <summary>
/// The warehouses the utility ships with: reference data, not demo data. A migrated database can
/// receive and issue stock (ARCHITECTURE.md invariant 8) with no seeder involved.
/// </summary>
/// <remarks>
/// <para>
/// <b>One store per island</b>, because that is how a utility spread across the Northern Marianas
/// actually holds stock: a crew on Rota cannot draw a connector from a shelf on Saipan, so the
/// island <i>is</i> the warehouse. Lower Base is the main store; Tinian and Rota hold what their own
/// crews work from.
/// </para>
/// <para>
/// This set replaced WP-0.8's invented main store / north depot / substation yard (WP-1.4). Adding
/// or changing one is a <b>new migration</b> — migrations are append-only (invariant 7) — and note
/// that a code change is an <i>id</i> change, because <c>ReferenceId</c> derives the id
/// from the code.
/// </para>
/// </remarks>
public static class DefaultWarehouses
{
    /// <summary>Code of the main store, at Lower Base on Saipan.</summary>
    public const string LowerBase = "LB";

    /// <summary>Code of the Tinian store.</summary>
    public const string Tinian = "TINIAN";

    /// <summary>Code of the Rota store.</summary>
    public const string Rota = "ROTA";

    /// <summary>
    /// The instant this reference set was authored, and the timestamp component of every warehouse
    /// id. Fixed forever: changing it changes every id, which to the database is a different set.
    /// </summary>
    public static readonly DateTimeOffset AuthoredAt = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Every warehouse, in code order.</summary>
    public static IReadOnlyList<Warehouse> All { get; } =
    [
        Warehouse.Reference(LowerBase, "Lower Base Warehouse", "Lower Base, Saipan"),
        Warehouse.Reference(Rota, "Rota Warehouse", "Songsong, Rota"),
        Warehouse.Reference(Tinian, "Tinian Warehouse", "San Jose, Tinian"),
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
