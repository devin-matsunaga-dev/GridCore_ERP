using GridCore.Modules.Metering.Data;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Modules.Metering.Seeding;
using GridCore.Platform;
using GridCore.Platform.Data;
using GridCore.Platform.Modules;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Metering;

/// <summary>Composition root for the Metering module. Slices live under <c>Features/</c>.</summary>
public sealed class MeteringModule : IModule
{
    /// <inheritdoc />
    public string Name => MeteringDbContext.SchemaName;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The metering schema, on the scope's shared connection so a registration, its audit entry
        // and its outbox row commit together.
        services.AddGridCoreDbContext<MeteringDbContext>((builder, connection) =>
            builder.UseNpgsql(connection, GridCoreDbContexts.InSchema(MeteringDbContext.SchemaName)));

        services.AddScoped<IMeterNumberGenerator, SequentialMeterNumberGenerator>();
        services.AddScoped<IMeterService, MeterService>();

        // Note what is NOT here: IServiceLocationDirectory. This module consumes it and the
        // Customers module registers it, which is the whole point of putting the interface in
        // Contracts — a module never registers another module's implementation, and never
        // references the assembly that holds one.

        // Edge validation. Registered one by one rather than by scanning, so the composition stays
        // greppable — the same reason Program.cs lists the modules.
        services.AddGridCoreValidator<RegisterMeterRequest, RegisterMeterRequestValidator>();
        services.AddGridCoreValidator<UpdateMeterRequest, UpdateMeterRequestValidator>();
        services.AddGridCoreValidator<AssignMeterRequest, AssignMeterRequestValidator>();
        services.AddGridCoreValidator<RemoveMeterRequest, RemoveMeterRequestValidator>();
        services.AddGridCoreValidator<ChangeMeterStatusRequest, ChangeMeterStatusRequestValidator>();

        // Registering a seeder does not make it run: DemoSeedRunner is only registered where the
        // environment allows it, so this line is unconditional and the guard stays in one place.
        services.AddDemoSeeder<MetersDemoSeeder>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapMeterEndpoints();
    }
}
