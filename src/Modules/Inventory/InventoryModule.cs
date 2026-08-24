using GridCore.Modules.Inventory.Data;
using GridCore.Platform.Data;
using GridCore.Platform.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Inventory;

/// <summary>Composition root for the Inventory module. Slices live under <c>Features/</c>.</summary>
public sealed class InventoryModule : IModule
{
    public string Name => "inventory";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The inventory schema, on the scope's shared connection so a stock movement, its audit
        // entry and the GoodsReceived outbox row commit together. It carries the warehouses
        // (WP-0.8); items and stock levels are WP-1.4's.
        services.AddGridCoreDbContext<InventoryDbContext>((builder, connection) =>
            builder.UseNpgsql(connection, GridCoreDbContexts.InSchema(InventoryDbContext.SchemaName)));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Endpoints are mapped per feature slice from WP-1.4 onwards.
    }
}
