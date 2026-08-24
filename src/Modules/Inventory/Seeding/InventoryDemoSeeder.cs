using GridCore.Modules.Inventory.Data;
using GridCore.Modules.Inventory.Features.Items;
using GridCore.Modules.Inventory.Features.Shared;
using GridCore.Modules.Inventory.Features.Warehouses;
using GridCore.Platform.Registry;
using GridCore.Platform.Seeding;

namespace GridCore.Modules.Inventory.Seeding;

/// <summary>
/// A small demo store — the conductor, hardware, spares and consumables a distribution utility on
/// three islands actually holds, with the movements that got them onto the shelves, so WP-3.3 has
/// parts to issue to a work order and WP-1.5's registry screens have a stock list to render.
/// Stock sits on the island whose crews draw it: the bulk of it at Lower Base on Saipan, with Rota
/// and Tinian holding what their own work needs.
/// </summary>
/// <remarks>
/// <para>
/// Every quantity here arrives through the real <see cref="StockItem.Receive"/>,
/// <see cref="StockItem.Issue"/> and <see cref="StockItem.Adjust"/> methods rather than being
/// assigned, so every seeded level has a ledger behind it that adds up to it — and a demo movement
/// that breaks a rule (issuing more than is on hand, receiving against a discontinued line) fails
/// here, at startup, rather than shipping a store whose count nothing explains.
/// </para>
/// <para>
/// The warehouses are reference data (WP-0.8) and their ids are derived from their codes, so this
/// seeder resolves them from <see cref="DefaultWarehouses"/> rather than querying: inside the
/// seeding transaction a query would see them, but deriving is exact and says out loud that a
/// warehouse is not something a demo invents.
/// </para>
/// <para>
/// Codes are assigned here rather than through <see cref="IStockItemNumberGenerator"/>: the
/// generator reads the highest code already committed, and inside the seeding transaction none of
/// these rows are visible to a query yet. Starting the series at 1 is what lets an item registered
/// afterwards continue it correctly.
/// </para>
/// </remarks>
public sealed class InventoryDemoSeeder(InventoryDbContext database, TimeProvider clock) : IDemoSeeder
{
    /// <summary>
    /// Who the seeded stock is attributed to — the same stand-in the approval queue's purchase order
    /// is raised by, so one <c>demo:</c> subject id never appears under two names.
    /// </summary>
    public static DemoActor Storeman { get; } = new("warehouse", "Wes Store (demo)");

    private static RegistryActor Attribution { get; } = RegistryActor.Of(Storeman);

    /// <inheritdoc />
    /// <remarks>The dedupe key. Never renamed — a rename seeds a second copy of this store.</remarks>
    public string Name => "inventory.stock";

    /// <inheritdoc />
    /// <remarks>
    /// After the customer registries (200, 300) and the plant register (400), before the work orders
    /// that will draw parts from these shelves.
    /// </remarks>
    public int Order => 500;

    /// <inheritdoc />
    public Task SeedAsync(CancellationToken cancellationToken)
    {
        // Ids are Guid v7 stamped from the instant they are created, and rows created in the same
        // instant have no defined order. A step per write keeps the catalogue list and each ledger
        // stable between runs.
        var now = clock.GetUtcNow();
        var step = 0;

        DateTimeOffset Next() => now.AddMilliseconds(step++);

        var ordinal = 0;

        foreach (var line in DemoStock)
        {
            var item = StockItem.Register(
                RegistryNumbers.Format(InventoryNumbers.ItemCodePrefix, ++ordinal),
                line.Category,
                line.Name,
                line.Unit,
                Next(),
                line.Description,
                line.PartNumber,
                line.UnitCost);

            // Reorder levels first, so a line that is on order but not yet delivered still shows up
            // on the low-stock report — which is the state a store spends most of its time in.
            foreach (var (warehouse, minimum) in line.Minimums)
            {
                item.SetMinimumQuantity(WarehouseId(warehouse), minimum, Next());
            }

            foreach (var movement in line.Movements)
            {
                Apply(item, movement, Next());
            }

            if (line.DiscontinuedBecause is { } reason)
            {
                // Discontinued last, after the stock has moved: that is the order it happens in, and
                // it is what proves a discontinued line keeps the ledger and the remainder on the
                // shelf rather than disappearing (there is no delete in this registry either).
                item.UpdateDetails(
                    line.Category,
                    line.Name,
                    line.Unit,
                    line.UnitCost,
                    isActive: false,
                    line.Description,
                    line.PartNumber,
                    reason);
            }

            database.StockItems.Add(item);
        }

        // No SaveChanges: the runner's unit of work saves this and the seed record in one
        // transaction, which is what makes a half-seeded demo world impossible.
        return Task.CompletedTask;
    }

    private static void Apply(StockItem item, DemoMovement movement, DateTimeOffset now)
    {
        var warehouseId = WarehouseId(movement.Warehouse);

        switch (movement.Type)
        {
            case StockMovementType.Receipt:
                item.Receive(warehouseId, movement.Quantity, Attribution, now, movement.UnitCost, movement.Reference, movement.Note);
                break;

            case StockMovementType.Issue:
                item.Issue(warehouseId, movement.Quantity, Attribution, now, reference: movement.Reference, note: movement.Note);
                break;

            case StockMovementType.Adjustment:
                item.Adjust(warehouseId, movement.Quantity, movement.Note!, Attribution, now);
                break;

            default:
                throw new InventoryValidationException($"'{movement.Type}' is not a movement this seeder knows how to demonstrate.");
        }
    }

    private static Guid WarehouseId(string code) => DefaultWarehouses.Require(code).Id;

    /// <summary>
    /// The demo store. Every category appears, both a low-stock shelf and a healthy one are present,
    /// and the awkward states a screen has to render are all here: a line on order with nothing on
    /// the shelf, a stock take that found less than the system said, and a discontinued line with a
    /// remainder still to be used up.
    /// </summary>
    private static IReadOnlyList<DemoStockLine> DemoStock { get; } =
    [
        new(StockItemCategory.Conductor, "ACSR Raven 1/0 conductor", UnitOfMeasure.Metre, 4.85m,
            "Bare overhead conductor, the standard lateral build",
            "ACSR-RAVEN-1/0",
            Minimums: [(DefaultWarehouses.LowerBase, 500m), (DefaultWarehouses.Rota, 200m)],
            Movements:
            [
                new(StockMovementType.Receipt, DefaultWarehouses.LowerBase, 2000m, 4.85m, "DN-4471", "Delivered to the Lower Base store"),
                new(StockMovementType.Receipt, DefaultWarehouses.Rota, 600m, 4.85m, "TR-0112", "Shipped over to Rota for the Sinapalo work"),
                new(StockMovementType.Issue, DefaultWarehouses.Rota, 180m, Note: "As Nieves lateral rebuild, Rota"),
            ]),

        new(StockItemCategory.Conductor, "Copper earth wire, 25 mm²", UnitOfMeasure.Metre, 6.20m,
            "Down-lead earthing for poles and substations",
            PartNumber: null,
            Minimums: [(DefaultWarehouses.LowerBase, 300m)],
            Movements:
            [
                new(StockMovementType.Receipt, DefaultWarehouses.LowerBase, 500m, 6.20m, "DN-4471"),
                new(StockMovementType.Issue, DefaultWarehouses.LowerBase, 60m, Note: "Earthing repairs, Garapan Beach Road"),
            ]),

        new(StockItemCategory.Hardware, "LV connector kit, 4-way", UnitOfMeasure.Each, 18.40m,
            "Insulated piercing connectors, service drops",
            "TE-LV4-CONN",
            Minimums: [(DefaultWarehouses.LowerBase, 40m), (DefaultWarehouses.Rota, 20m)],
            Movements:
            [
                new(StockMovementType.Receipt, DefaultWarehouses.LowerBase, 120m, 18.40m, "DN-4502"),
                new(StockMovementType.Receipt, DefaultWarehouses.Rota, 40m, 18.40m, "TR-0113", "Shipped over to Rota"),

                // Leaves Rota on 14 against a reorder level of 20: the low-stock row a storeman is
                // meant to act on.
                new(StockMovementType.Issue, DefaultWarehouses.Rota, 26m, Note: "Service reconnections, Sinapalo"),
            ]),

        new(StockItemCategory.Hardware, "Surge arrester, 11 kV", UnitOfMeasure.Each, 96.00m,
            "Pole-top arrester, distribution laterals",
            "ARR-11KV-DIST",
            Minimums: [(DefaultWarehouses.Tinian, 6m)],
            Movements:
            [
                new(StockMovementType.Receipt, DefaultWarehouses.Tinian, 10m, 96.00m, "DN-4502"),
                new(StockMovementType.Issue, DefaultWarehouses.Tinian, 5m, Note: "Lightning damage, Tinian San Jose lateral"),
            ]),

        new(StockItemCategory.Transformer, "Pole-mount transformer, 15 kVA", UnitOfMeasure.Each, 2450.00m,
            "Network spare. Becomes a tagged asset once it is installed",
            "ABB-15KVA-PM",
            Minimums: [(DefaultWarehouses.Tinian, 2m)],
            Movements:
            [
                new(StockMovementType.Receipt, DefaultWarehouses.Tinian, 4m, 2450.00m, "DN-4390", "Annual spares order"),
                new(StockMovementType.Issue, DefaultWarehouses.Tinian, 1m, Note: "Replacement for T-7, San Jose, Tinian"),
            ]),

        new(StockItemCategory.Metering, "Single-phase meter, 100 A", UnitOfMeasure.Each, 82.50m,
            "Boxed and unfitted. The fitted meter is Metering's own record, not this line",
            "ITR-C1SR-100",
            Minimums: [(DefaultWarehouses.LowerBase, 50m)],
            Movements:
            [
                new(StockMovementType.Receipt, DefaultWarehouses.LowerBase, 200m, 82.50m, "DN-4415", "Meter replacement programme"),
                new(StockMovementType.Issue, DefaultWarehouses.LowerBase, 12m, Note: "New connections, Saipan Chalan Kanoa"),
            ]),

        new(StockItemCategory.Consumable, "Transformer oil, mineral", UnitOfMeasure.Litre, 3.15m,
            "Topping up and refilling after a gasket repair",
            PartNumber: null,
            Minimums: [(DefaultWarehouses.LowerBase, 200m)],
            Movements:
            [
                new(StockMovementType.Receipt, DefaultWarehouses.LowerBase, 1000m, 3.15m, "DN-4390"),
                new(StockMovementType.Issue, DefaultWarehouses.LowerBase, 45.5m, Note: "Gasket replacement, Garapan T-11"),

                // A stock take that found less than the system said — the adjustment a demo needs in
                // order to show the sensitive path at all. The counted figure is what is stated; the
                // shortfall is derived.
                new(StockMovementType.Adjustment, DefaultWarehouses.LowerBase, 940m,
                    Note: "Annual stock take: drum found short after a spill in the Lower Base store"),
            ]),

        new(StockItemCategory.Safety, "Insulated gloves, class 2", UnitOfMeasure.Each, 145.00m,
            "Issued per linesman, replaced on test failure",
            PartNumber: null,
            Minimums: [(DefaultWarehouses.Rota, 8m)],
            Movements:
            [
                new(StockMovementType.Receipt, DefaultWarehouses.Rota, 12m, 145.00m, "DN-4415"),
                new(StockMovementType.Issue, DefaultWarehouses.Rota, 6m, Note: "Issued to the Rota line crew"),
            ]),

        new(StockItemCategory.Tooling, "Hot stick, 8 ft", UnitOfMeasure.Each, 320.00m,
            "Signed out per job, returned to the depot",
            PartNumber: null,
            Minimums: [(DefaultWarehouses.Rota, 2m)],
            Movements: [new(StockMovementType.Receipt, DefaultWarehouses.Rota, 3m, 320.00m, "DN-4390")]),

        new(StockItemCategory.Hardware, "Composite crossarm, 2.4 m", UnitOfMeasure.Each, 132.00m,
            "On order against the pole replacement programme; nothing delivered yet",
            "COMP-XARM-24",
            Minimums: [(DefaultWarehouses.LowerBase, 10m)],
            Movements: []),

        new(StockItemCategory.Hardware, "Wooden crossarm, 2.4 m", UnitOfMeasure.Each, 88.00m,
            "Superseded by the composite crossarm",
            PartNumber: null,
            Minimums: [],
            Movements:
            [
                new(StockMovementType.Receipt, DefaultWarehouses.LowerBase, 40m, 88.00m, "DN-4102"),
                new(StockMovementType.Issue, DefaultWarehouses.LowerBase, 38m, Note: "Pole rebuilds, As Nieves Road"),
            ],
            DiscontinuedBecause: "Superseded by the composite crossarm; the remaining two are to be used up"),
    ];

    /// <param name="Type">Which way the stock went.</param>
    /// <param name="Warehouse">The code of the warehouse it moved in.</param>
    /// <param name="Quantity">How much moved — or, on an adjustment, what was counted.</param>
    /// <param name="UnitCost">What one unit cost, on a receipt.</param>
    /// <param name="Reference">The delivery note or docket.</param>
    /// <param name="Note">What happened. The reason, on an adjustment, where it is required.</param>
    private sealed record DemoMovement(
        StockMovementType Type,
        string Warehouse,
        decimal Quantity,
        decimal? UnitCost = null,
        string? Reference = null,
        string? Note = null);

    /// <param name="Category">What kind of thing it is.</param>
    /// <param name="Name">What it is.</param>
    /// <param name="Unit">What one of it is.</param>
    /// <param name="UnitCost">What one unit is reckoned to cost.</param>
    /// <param name="Description">What a storeman is told about it.</param>
    /// <param name="PartNumber">The manufacturer's part number, where the item has one.</param>
    /// <param name="Minimums">The reorder levels to set, by warehouse code.</param>
    /// <param name="Movements">The movements to walk it through, in order.</param>
    /// <param name="DiscontinuedBecause">Why the line was discontinued afterwards, where it was.</param>
    private sealed record DemoStockLine(
        StockItemCategory Category,
        string Name,
        UnitOfMeasure Unit,
        decimal UnitCost,
        string? Description,
        string? PartNumber,
        IReadOnlyList<(string Warehouse, decimal Minimum)> Minimums,
        IReadOnlyList<DemoMovement> Movements,
        string? DiscontinuedBecause = null);
}
