using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Platform.Modules;

/// <summary>Host-side wiring for <see cref="IModule"/> implementations.</summary>
public static class ModuleRegistration
{
    /// <summary>
    /// Registers every module with the container and keeps the ordered list available
    /// for endpoint mapping. Modules are supplied explicitly by the host rather than
    /// discovered by assembly scanning, so the composition is greppable.
    /// </summary>
    public static IReadOnlyList<IModule> AddModules(
        this IServiceCollection services,
        IConfiguration configuration,
        params IModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(modules);

        var duplicate = modules
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate module name '{duplicate.Key}'. Module names are also Postgres schema names and must be unique.");
        }

        foreach (var module in modules)
        {
            module.AddServices(services, configuration);
            services.AddSingleton(module);
        }

        return modules;
    }

    /// <summary>Maps each registered module's endpoints.</summary>
    public static WebApplication MapModules(this WebApplication app, IReadOnlyList<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(modules);

        foreach (var module in modules)
        {
            module.MapEndpoints(app);
        }

        return app;
    }
}
