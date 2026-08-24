using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.Seeding;
using GridCore.Platform;
using GridCore.Platform.Data;
using GridCore.Platform.Modules;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Customers;

/// <summary>Composition root for the Customers module. Slices live under <c>Features/</c>.</summary>
public sealed class CustomersModule : IModule
{
    /// <inheritdoc />
    public string Name => CustomersDbContext.SchemaName;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The customers schema, on the scope's shared connection so a registration, its audit entry
        // and its outbox row commit together.
        services.AddGridCoreDbContext<CustomersDbContext>((builder, connection) =>
            builder.UseNpgsql(connection, GridCoreDbContexts.InSchema(CustomersDbContext.SchemaName)));

        services.AddScoped<IRegistryNumberGenerator, SequentialRegistryNumberGenerator>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IServiceLocationService, ServiceLocationService>();

        // Edge validation. Registered one by one rather than by scanning, so the composition stays
        // greppable — the same reason Program.cs lists the modules.
        services.AddGridCoreValidator<CreateCustomerRequest, CreateCustomerRequestValidator>();
        services.AddGridCoreValidator<UpdateCustomerRequest, UpdateCustomerRequestValidator>();
        services.AddGridCoreValidator<ChangeCustomerStatusRequest, ChangeCustomerStatusRequestValidator>();
        services.AddGridCoreValidator<ServiceLocationRequest, ServiceLocationRequestValidator>();

        // Registering a seeder does not make it run: DemoSeedRunner is only registered where the
        // environment allows it, so this line is unconditional and the guard stays in one place.
        services.AddDemoSeeder<CustomersDemoSeeder>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapCustomerEndpoints();
        endpoints.MapServiceLocationEndpoints();
    }
}
