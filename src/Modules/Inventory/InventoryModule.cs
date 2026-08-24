using GridCore.Modules.Inventory.Data;
using GridCore.Modules.Inventory.Features.Items;
using GridCore.Modules.Inventory.Features.Shared;
using GridCore.Modules.Inventory.Features.Warehouses;
using GridCore.Modules.Inventory.Seeding;
using GridCore.Platform;
using GridCore.Platform.Data;
using GridCore.Platform.Modules;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Inventory;

/// <summary>Composition root for the Inventory module. Slices live under <c>Features/</c>.</summary>
public sealed class InventoryModule : IModule
{
    /// <inheritdoc />
    public string Name => InventoryDbContext.SchemaName;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The inventory schema, on the scope's shared connection so a stock movement, its ledger
        // line and its audit entry commit together — and so WP-4.1's GoodsReceived outbox row will
        // join the same transaction with nothing here to change.
        services.AddGridCoreDbContext<InventoryDbContext>((builder, connection) =>
            builder.UseNpgsql(connection, GridCoreDbContexts.InSchema(InventoryDbContext.SchemaName)));

        services.AddScoped<IStockItemNumberGenerator, SequentialStockItemNumberGenerator>();
        services.AddScoped<IStockItemService, StockItemService>();
        services.AddScoped<IWarehouseService, WarehouseService>();

        // Edge validation. Registered one by one rather than by scanning, so the composition stays
        // greppable — the same reason Program.cs lists the modules.
        services.AddGridCoreValidator<RegisterStockItemRequest, RegisterStockItemRequestValidator>();
        services.AddGridCoreValidator<UpdateStockItemRequest, UpdateStockItemRequestValidator>();
        services.AddGridCoreValidator<ReceiveStockRequest, ReceiveStockRequestValidator>();
        services.AddGridCoreValidator<IssueStockRequest, IssueStockRequestValidator>();
        services.AddGridCoreValidator<AdjustStockRequest, AdjustStockRequestValidator>();
        services.AddGridCoreValidator<SetMinimumQuantityRequest, SetMinimumQuantityRequestValidator>();

        // Registering a seeder does not make it run: DemoSeedRunner is only registered where the
        // environment allows it, so this line is unconditional and the guard stays in one place.
        services.AddDemoSeeder<InventoryDemoSeeder>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapWarehouseEndpoints();
        endpoints.MapStockItemEndpoints();
    }
}
