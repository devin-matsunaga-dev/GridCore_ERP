using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.Search;
using GridCore.Modules.Customers.Features.ServiceAccounts;
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
        services.AddScoped<IServiceAccountService, ServiceAccountService>();

        // Intake (WP-2.8). The deposit schedule is read-only reference data; the registration
        // service composes the three registries above inside one unit of work.
        services.AddScoped<IDepositRuleService, DepositRuleService>();
        services.AddScoped<ICustomerRegistrationService, CustomerRegistrationService>();

        // CSR search (WP-2.9). Read-only, and the one service in this module that consumes another
        // module's seam: IMeterDirectory is registered by Metering, so a meter number can be
        // resolved to a premise without this module knowing a metering schema exists.
        services.AddScoped<ICustomerSearchService, CustomerSearchService>();

        // The premise registry as the rest of GridCore reads it (WP-2.1). Registered against the
        // Contracts interface rather than the concrete type: this is the one place that knows both
        // halves, and a consumer never learns a customers schema exists.
        services.AddScoped<IServiceLocationDirectory, ServiceLocationDirectory>();

        // The service account registry as the rest of GridCore reads it (WP-2.3). Billing raises a
        // bill against "the account open at the premise this meter is on" — a derivation WP-2.1
        // named and this is what answers it, without Billing learning that a customers schema
        // exists. Registered here for the same reason the premise directory is.
        services.AddScoped<IServiceAccountDirectory, ServiceAccountDirectory>();

        // Edge validation. Registered one by one rather than by scanning, so the composition stays
        // greppable — the same reason Program.cs lists the modules.
        services.AddGridCoreValidator<CreateCustomerRequest, CreateCustomerRequestValidator>();
        services.AddGridCoreValidator<UpdateCustomerRequest, UpdateCustomerRequestValidator>();
        services.AddGridCoreValidator<ChangeCustomerStatusRequest, ChangeCustomerStatusRequestValidator>();
        services.AddGridCoreValidator<ServiceLocationRequest, ServiceLocationRequestValidator>();
        services.AddGridCoreValidator<OpenServiceAccountRequest, OpenServiceAccountRequestValidator>();
        services.AddGridCoreValidator<ServiceAccountTransitionRequest, ServiceAccountTransitionRequestValidator>();
        services.AddGridCoreValidator<RegisterCustomerIntakeRequest, RegisterCustomerIntakeRequestValidator>();

        // Registering a seeder does not make it run: DemoSeedRunner is only registered where the
        // environment allows it, so this line is unconditional and the guard stays in one place.
        services.AddDemoSeeder<CustomersDemoSeeder>();
        services.AddDemoSeeder<ServiceAccountsDemoSeeder>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapCustomerEndpoints();
        endpoints.MapServiceLocationEndpoints();
        endpoints.MapServiceAccountEndpoints();
        endpoints.MapRegistrationEndpoints();
        endpoints.MapCustomerSearchEndpoints();
    }
}
