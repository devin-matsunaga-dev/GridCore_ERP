using GridCore.Modules.Inventory.Features.Warehouses;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Inventory.Data;

/// <summary>
/// The Inventory module's schema. Today it holds the warehouses; WP-1.4 adds the items and stock
/// levels they hold, and WP-4.1 the purchasing that fills them.
/// </summary>
public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    /// <summary>The Postgres schema this context owns — also the module's name.</summary>
    public const string SchemaName = "inventory";

    /// <summary>The places stock is held.</summary>
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);
    }
}
