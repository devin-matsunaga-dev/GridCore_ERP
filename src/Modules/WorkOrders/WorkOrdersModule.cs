using GridCore.Platform.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.WorkOrders;

/// <summary>Composition root for the WorkOrders module. Slices live under <c>Features/</c>.</summary>
public sealed class WorkOrdersModule : IModule
{
    public string Name => "work_orders";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Registered per feature slice from WP-1.x onwards.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Endpoints are mapped per feature slice from WP-1.x onwards.
    }
}
