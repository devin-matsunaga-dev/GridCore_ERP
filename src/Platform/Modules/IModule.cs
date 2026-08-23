using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Platform.Modules;

/// <summary>
/// A module of the modular monolith. Each module owns its schema, its services and its
/// endpoints; the host discovers modules and calls these two hooks. Modules never reach
/// into one another — cross-module reads go through service interfaces, cross-module
/// effects through domain events.
/// </summary>
public interface IModule
{
    /// <summary>Stable module name; also the Postgres schema the module owns.</summary>
    string Name { get; }

    /// <summary>Register the module's services (DbContext, handlers, providers).</summary>
    void AddServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Map the module's endpoints under its own route prefix.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
