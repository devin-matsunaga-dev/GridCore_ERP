namespace GridCore.Platform.Seeding;

/// <summary>
/// A module's contribution to the demo world: the small utility dataset SPEC.md calls for
/// (customers, locations, meters, assets, bills, inventory, work orders), each module seeding its
/// own schema through its own services.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>demo</b> data, never reference data. Reference data — the chart of accounts, rate
/// plans, warehouses — ships by migration because the app does not work without it (ARCHITECTURE.md
/// invariant 8); a demo seeder only ever adds things a demo is nicer for having, and a
/// never-seeded database is fully functional.
/// </para>
/// <para>
/// Seeders never run outside Development. <see cref="DemoSeedRunner"/> refuses regardless of
/// configuration, so an implementation does not have to guard itself.
/// </para>
/// </remarks>
public interface IDemoSeeder
{
    /// <summary>
    /// Stable name of this seeder, recorded in <c>platform.demo_seed_records</c> once it has run.
    /// <b>Never renamed</b> — the name is the "has this already run" key, so a rename seeds a
    /// second copy of the same demo world.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Relative order within a seeding run, low first. Modules seed in dependency order (a work
    /// order needs its asset), and ties are broken by <see cref="Name"/> so a run is reproducible.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Writes this seeder's demo data. Called inside an <see cref="Data.IUnitOfWork"/> transaction
    /// that also records the run, so an implementation adds entities and never saves: a seeder that
    /// throws half way leaves neither its rows nor its "already run" record behind.
    /// </summary>
    Task SeedAsync(CancellationToken cancellationToken);
}
