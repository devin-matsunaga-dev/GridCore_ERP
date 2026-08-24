using GridCore.Modules.Assets.Data;
using GridCore.Modules.Assets.Features.Assets;
using GridCore.Modules.Assets.Features.Shared;
using GridCore.Modules.Assets.Seeding;
using GridCore.Platform;
using GridCore.Platform.Data;
using GridCore.Platform.Modules;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Assets;

/// <summary>Composition root for the Assets module. Slices live under <c>Features/</c>.</summary>
public sealed class AssetsModule : IModule
{
    /// <inheritdoc />
    public string Name => AssetsDbContext.SchemaName;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The assets schema, on the scope's shared connection so a registration, its audit entry
        // and its outbox row commit together.
        services.AddGridCoreDbContext<AssetsDbContext>((builder, connection) =>
            builder.UseNpgsql(connection, GridCoreDbContexts.InSchema(AssetsDbContext.SchemaName)));

        services.AddScoped<IAssetNumberGenerator, SequentialAssetNumberGenerator>();
        services.AddScoped<IAssetService, AssetService>();

        // Edge validation. Registered one by one rather than by scanning, so the composition stays
        // greppable — the same reason Program.cs lists the modules.
        services.AddGridCoreValidator<RegisterAssetRequest, RegisterAssetRequestValidator>();
        services.AddGridCoreValidator<UpdateAssetRequest, UpdateAssetRequestValidator>();
        services.AddGridCoreValidator<ChangeAssetStatusRequest, ChangeAssetStatusRequestValidator>();
        services.AddGridCoreValidator<AssessAssetConditionRequest, AssessAssetConditionRequestValidator>();

        // Registering a seeder does not make it run: DemoSeedRunner is only registered where the
        // environment allows it, so this line is unconditional and the guard stays in one place.
        services.AddDemoSeeder<AssetsDemoSeeder>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapAssetEndpoints();
    }
}
