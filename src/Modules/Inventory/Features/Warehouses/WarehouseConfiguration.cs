using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridCore.Modules.Inventory.Features.Warehouses;

/// <summary>Maps <see cref="Warehouse"/> onto <c>inventory.warehouses</c> and seeds the shipped set.</summary>
public sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("warehouses");

        builder.HasKey(warehouse => warehouse.Id).HasName("pk_warehouses");

        builder.Property(warehouse => warehouse.Id).HasColumnName("id");
        builder.Property(warehouse => warehouse.Code).HasColumnName("code").HasMaxLength(Warehouse.CodeLength).IsRequired();
        builder.Property(warehouse => warehouse.Name).HasColumnName("name").HasMaxLength(Warehouse.NameLength).IsRequired();
        builder.Property(warehouse => warehouse.Location).HasColumnName("location").HasMaxLength(Warehouse.LocationLength);
        builder.Property(warehouse => warehouse.IsActive).HasColumnName("is_active");

        builder.HasIndex(warehouse => warehouse.Code).HasDatabaseName("ux_warehouses_code").IsUnique();

        builder.HasData(DefaultWarehouses.All);
    }
}
