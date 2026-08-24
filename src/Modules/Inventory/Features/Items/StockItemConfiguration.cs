using GridCore.Modules.Inventory.Features.Warehouses;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Inventory.Features.Items;

/// <summary>Maps <see cref="StockItem"/> onto <c>inventory.stock_items</c>.</summary>
public sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("stock_items");

        builder.HasKey(item => item.Id).HasName("pk_stock_items");

        // Never store-generated: every id in GridCore is a Guid v7 minted in code from the clock,
        // and saying so is load-bearing. EF decides whether an untracked child of a tracked parent
        // is an insert or an update by asking whether its key is set on a store-generated column —
        // leave the default and a freshly appended movement is tracked as Modified, and the save
        // fails having updated nothing (WP-1.2 lost half an hour to this).
        builder.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(item => item.ItemCode)
            .HasColumnName("item_code")
            .HasMaxLength(RegistryNumbers.MaxLength)
            .IsRequired();

        builder.Property(item => item.Name).HasColumnName("name").HasMaxLength(StockItem.NameLength).IsRequired();

        // Stored by name: a record read years from now must not depend on today's enum ordering.
        builder.Property(item => item.Category)
            .HasColumnName("category")
            .HasConversion<string>()
            .HasMaxLength(StockItem.EnumNameLength)
            .IsRequired();

        builder.Property(item => item.Unit)
            .HasColumnName("unit")
            .HasConversion<string>()
            .HasMaxLength(StockItem.EnumNameLength)
            .IsRequired();

        builder.Property(item => item.Description).HasColumnName("description").HasMaxLength(StockItem.DescriptionLength);

        builder.Property(item => item.ManufacturerPartNumber)
            .HasColumnName("manufacturer_part_number")
            .HasMaxLength(StockItem.PartNumberLength);

        builder.Property(item => item.UnitCost)
            .HasColumnName("unit_cost")
            .HasPrecision(StockCosts.Precision, StockCosts.DecimalPlaces);

        builder.Property(item => item.IsActive).HasColumnName("is_active");
        builder.Property(item => item.StatusReason).HasColumnName("status_reason").HasMaxLength(StockItem.ReasonLength);
        builder.Property(item => item.RegisteredAt).HasColumnName("registered_at");

        // Totals over the levels, computed on the loaded aggregate. The list query answers the same
        // questions in SQL (see StockLevel.BelowMinimum) rather than pulling every level to sum it.
        builder.Ignore(item => item.TotalOnHand);
        builder.Ignore(item => item.IsBelowMinimum);

        // Owned by the item and loaded through it. Backing fields rather than the properties: both
        // collections are IReadOnlyList so nothing outside the aggregate can move a quantity or
        // write a ledger line.
        builder.HasMany(item => item.Levels)
            .WithOne()
            .HasForeignKey(level => level.StockItemId)
            .HasConstraintName("fk_stock_levels_stock_item")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(StockItem.Levels))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(item => item.Movements)
            .WithOne()
            .HasForeignKey(movement => movement.StockItemId)
            .HasConstraintName("fk_stock_movements_stock_item")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(StockItem.Movements))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(item => item.ItemCode).HasDatabaseName("ux_stock_items_item_code").IsUnique();

        // One part, one catalogue line. Unfiltered rather than filtered on "not null": both Postgres
        // and the fast tier's SQLite treat NULLs in a unique index as distinct, so the many items
        // that carry no manufacturer's number are unaffected — and this needs no hand-written SQL
        // predicate to keep in step with the column name (WP-1.2's lesson).
        builder.HasIndex(item => item.ManufacturerPartNumber)
            .HasDatabaseName("ux_stock_items_manufacturer_part_number")
            .IsUnique();

        // The registry screen filters on these two.
        builder.HasIndex(item => item.Category).HasDatabaseName("ix_stock_items_category");
        builder.HasIndex(item => item.IsActive).HasDatabaseName("ix_stock_items_is_active");
    }
}

/// <summary>Maps <see cref="StockLevel"/> onto <c>inventory.stock_levels</c>.</summary>
public sealed class StockLevelConfiguration : IEntityTypeConfiguration<StockLevel>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<StockLevel> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("stock_levels");

        builder.HasKey(level => level.Id).HasName("pk_stock_levels");

        builder.Property(level => level.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(level => level.StockItemId).HasColumnName("stock_item_id");
        builder.Property(level => level.WarehouseId).HasColumnName("warehouse_id");

        builder.Property(level => level.QuantityOnHand)
            .HasColumnName("quantity_on_hand")
            .HasPrecision(StockQuantities.Precision, StockQuantities.DecimalPlaces);

        builder.Property(level => level.MinimumQuantity)
            .HasColumnName("minimum_quantity")
            .HasPrecision(StockQuantities.Precision, StockQuantities.DecimalPlaces);

        builder.Property(level => level.LastMovedAt).HasColumnName("last_moved_at");

        // Computed from the two columns beside it, never stored: a stored flag goes stale the moment
        // a movement forgets to refresh it. The list query uses StockLevel.BelowMinimum instead.
        builder.Ignore(level => level.IsBelowMinimum);

        // A warehouse is reference data in this same schema, so unlike the work-order id on a
        // movement this is a real foreign key the database enforces. Restricted rather than
        // cascading: nothing deletes a warehouse, and if anything ever tries, the stock held there
        // is exactly the reason it must not.
        builder.HasOne<Warehouse>()
            .WithMany()
            .HasForeignKey(level => level.WarehouseId)
            .HasConstraintName("fk_stock_levels_warehouse")
            .OnDelete(DeleteBehavior.Restrict);

        // One shelf per item per warehouse. Without this a second level could be opened for the same
        // pair by a race between two first deliveries, and the item would hold two half-counts.
        builder.HasIndex(level => new { level.StockItemId, level.WarehouseId })
            .HasDatabaseName("ux_stock_levels_item_warehouse")
            .IsUnique();

        // "What does this warehouse hold" is the other way round the store reads it.
        builder.HasIndex(level => level.WarehouseId).HasDatabaseName("ix_stock_levels_warehouse_id");
    }
}

/// <summary>Maps <see cref="StockMovement"/> onto <c>inventory.stock_movements</c>.</summary>
public sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("stock_movements");

        builder.HasKey(movement => movement.Id).HasName("pk_stock_movements");

        builder.Property(movement => movement.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(movement => movement.StockItemId).HasColumnName("stock_item_id");
        builder.Property(movement => movement.WarehouseId).HasColumnName("warehouse_id");

        builder.Property(movement => movement.MovementType)
            .HasColumnName("movement_type")
            .HasConversion<string>()
            .HasMaxLength(StockItem.EnumNameLength)
            .IsRequired();

        builder.Property(movement => movement.QuantityChange)
            .HasColumnName("quantity_change")
            .HasPrecision(StockQuantities.Precision, StockQuantities.DecimalPlaces);

        builder.Property(movement => movement.QuantityOnHandAfter)
            .HasColumnName("quantity_on_hand_after")
            .HasPrecision(StockQuantities.Precision, StockQuantities.DecimalPlaces);

        builder.Property(movement => movement.UnitCost)
            .HasColumnName("unit_cost")
            .HasPrecision(StockCosts.Precision, StockCosts.DecimalPlaces);

        builder.Property(movement => movement.Reference)
            .HasColumnName("reference")
            .HasMaxLength(StockMovement.ReferenceLength);

        // No foreign key: Work Orders is another module and another schema, and this module must
        // never reach into its tables. WP-3.3 stamps the id; a screen showing the job resolves it
        // through that module's service, not through a join.
        builder.Property(movement => movement.WorkOrderId).HasColumnName("work_order_id");

        builder.Property(movement => movement.Note).HasColumnName("note").HasMaxLength(StockMovement.NoteLength);
        builder.Property(movement => movement.ActorId).HasColumnName("actor_id").HasMaxLength(RegistryActor.MaxLength).IsRequired();
        builder.Property(movement => movement.ActorName).HasColumnName("actor_name").HasMaxLength(RegistryActor.MaxLength);
        builder.Property(movement => movement.RecordedAt).HasColumnName("recorded_at");

        builder.Ignore(movement => movement.Value);

        builder.HasOne<Warehouse>()
            .WithMany()
            .HasForeignKey(movement => movement.WarehouseId)
            .HasConstraintName("fk_stock_movements_warehouse")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(movement => movement.StockItemId).HasDatabaseName("ix_stock_movements_stock_item_id");
        builder.HasIndex(movement => movement.WarehouseId).HasDatabaseName("ix_stock_movements_warehouse_id");

        // WP-3.3 reads the issues on their own — "what has gone out to jobs" is a filter on a table
        // that grows with every delivery and every stock take.
        builder.HasIndex(movement => movement.MovementType).HasDatabaseName("ix_stock_movements_movement_type");
    }
}
