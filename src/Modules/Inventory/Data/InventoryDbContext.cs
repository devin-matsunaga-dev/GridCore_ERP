using GridCore.Modules.Inventory.Features.Items;
using GridCore.Modules.Inventory.Features.Warehouses;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Inventory.Data;

/// <summary>
/// The Inventory module's schema: the warehouses stock is held in, the catalogue of what is held,
/// how much of each is on each shelf, and every movement that got it there.
/// </summary>
public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    /// <summary>The Postgres schema this context owns — also the module's name.</summary>
    public const string SchemaName = "inventory";

    /// <summary>The places stock is held. Reference data, shipped by migration (WP-0.8).</summary>
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    /// <summary>The store's catalogue.</summary>
    public DbSet<StockItem> StockItems => Set<StockItem>();

    /// <summary>
    /// How much of each item each warehouse holds. Exposed as a set of its own so "what is low
    /// across the whole store" is one query rather than a walk over every catalogue line.
    /// </summary>
    public DbSet<StockLevel> StockLevels => Set<StockLevel>();

    /// <summary>
    /// Every movement of every item, append-only. Exposed as a set of its own so one item's ledger
    /// can be read without loading the item — and so a write can append to it without reading it.
    /// </summary>
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);
    }
}
