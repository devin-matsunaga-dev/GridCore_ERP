using GridCore.Platform.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Assets;

/// <summary>Composition root for the Assets module. Slices live under <c>Features/</c>.</summary>
public sealed class AssetsModule : IModule
{
    public string Name => "assets";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Registered per feature slice from WP-1.x onwards.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Endpoints are mapped per feature slice from WP-1.x onwards.
    }
}
