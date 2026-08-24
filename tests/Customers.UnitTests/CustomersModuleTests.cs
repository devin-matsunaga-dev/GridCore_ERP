using FluentValidation;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.Seeding;
using GridCore.Platform.Data;
using GridCore.Platform.Modules;
using GridCore.Platform.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Customers.UnitTests;

public class CustomersModuleTests
{
    private static IServiceCollection ComposedModule()
    {
        var services = new ServiceCollection();

        new CustomersModule().AddServices(services, new ConfigurationBuilder().Build());

        return services;
    }

    private static bool Registers<TService>(IServiceCollection services) =>
        services.Any(descriptor => descriptor.ServiceType == typeof(TService));

    [Fact]
    public void Module_declares_a_snake_case_schema_name()
    {
        IModule module = new CustomersModule();

        Assert.False(string.IsNullOrWhiteSpace(module.Name));
        Assert.Matches("^[a-z][a-z0-9_]*$", module.Name);
    }

    [Fact]
    public void The_module_name_is_the_schema_its_context_owns() =>
        // They are the same thing, and ModuleRegistration rejects two modules claiming one schema —
        // so a divergence here would show up as a module silently writing into another's tables.
        Assert.Equal(CustomersDbContext.SchemaName, new CustomersModule().Name);

    [Fact]
    public void The_context_joins_the_shared_transaction_rather_than_opening_its_own()
    {
        // AddGridCoreDbContext also enlists the context as a UnitOfWorkParticipant. A plain
        // AddDbContext would give it its own connection, and a customer row would then commit
        // separately from its audit entry and its outbox row — invariants 1 and 2, silently broken.
        var services = ComposedModule();

        Assert.True(Registers<CustomersDbContext>(services));
        Assert.True(Registers<UnitOfWorkParticipant>(services));
    }

    [Fact]
    public void The_registry_services_are_registered()
    {
        var services = ComposedModule();

        Assert.True(Registers<ICustomerService>(services));
        Assert.True(Registers<IServiceLocationService>(services));
        Assert.True(Registers<IServiceAccountService>(services));
        Assert.True(Registers<IRegistryNumberGenerator>(services));
    }

    [Fact]
    public void Every_request_body_the_endpoints_accept_has_a_validator_registered()
    {
        // The filter throws when a validator is missing, which would turn a mistake here into a 500
        // on the first registration anyone attempts. This is that mistake, caught in the fast loop.
        var services = ComposedModule();

        Assert.True(Registers<IValidator<CreateCustomerRequest>>(services));
        Assert.True(Registers<IValidator<UpdateCustomerRequest>>(services));
        Assert.True(Registers<IValidator<ChangeCustomerStatusRequest>>(services));
        Assert.True(Registers<IValidator<ServiceLocationRequest>>(services));
        Assert.True(Registers<IValidator<OpenServiceAccountRequest>>(services));
        Assert.True(Registers<IValidator<ServiceAccountTransitionRequest>>(services));
    }

    [Theory]
    [InlineData(typeof(CustomersDemoSeeder))]
    [InlineData(typeof(ServiceAccountsDemoSeeder))]
    public void The_demo_seeders_are_registered_unconditionally(Type seeder)
    {
        // Registering one does not run it: DemoSeedRunner is only registered where DemoSeedGuard
        // allows it, so the environment rule stays in one place rather than in every module.
        var services = ComposedModule();

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IDemoSeeder) && descriptor.ImplementationType == seeder);
    }
}
