using GridCore.Contracts.Directories;
using GridCore.Modules.Metering.Data;
using GridCore.Contracts.Providers;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Modules.Metering.Seeding;
using GridCore.Modules.Metering.Simulation;
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
        services.AddScoped<IMeterReadingService, MeterReadingService>();

        // The reading register as the rest of GridCore reads it (WP-2.3). Billing bills from
        // readings and may not touch this schema, so it takes IMeterReadingDirectory from Contracts
        // and this module — the only one that knows both halves — registers the implementation.
        services.AddScoped<IMeterReadingDirectory, MeterReadingDirectory>();

        // The meter register as the rest of GridCore reads it (WP-2.9). Customers turns the number
        // a caller quotes off their wall into the premise it measures, then into whoever is served
        // there — the first of those two hops is this seam, because Metering knows the meter and
        // Customers knows the customer and neither may read the other's schema.
        services.AddScoped<IMeterDirectory, MeterDirectory>();

        // The simulation seam. Metering owns the meter simulator (ARCHITECTURE.md's module table),
        // so unlike the premise directory this one IS registered here — but only ever against the
        // Contracts interface, which is what lets a production deployment swap in an AMI head-end by
        // changing this line and nothing else (invariant 6).
        services.AddSingleton<IMeterReadingProvider, SimulatedMeterReadingProvider>();

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
        services.AddGridCoreValidator<RecordMeterReadingRequest, RecordMeterReadingRequestValidator>();
        services.AddGridCoreValidator<RunReadingCycleRequest, RunReadingCycleRequestValidator>();

        // Registering a seeder does not make it run: DemoSeedRunner is only registered where the
        // environment allows it, so this line is unconditional and the guard stays in one place.
        services.AddDemoSeeder<MetersDemoSeeder>();
        services.AddDemoSeeder<MeterReadingsDemoSeeder>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapMeterEndpoints();
        endpoints.MapMeterReadingEndpoints();
    }
}
