using FluentValidation;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Transitions;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Notes;
using GridCore.Modules.Customers.Features.Search;
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
    public void Both_registries_this_module_publishes_are_registered_through_Contracts()
    {
        // The two cross-module read seams Customers owns: the premise directory (WP-2.1, consumed by
        // Metering) and the service account directory (WP-2.3, consumed by Billing). This module is
        // the only place that knows both halves of each, so it registers them — always against the
        // Contracts interface, never the concrete type, or a consumer could hold this module's EF
        // context by another name.
        var services = ComposedModule();

        Assert.Equal(
            typeof(ServiceLocationDirectory),
            Assert.Single(services, service => service.ServiceType == typeof(Contracts.Directories.IServiceLocationDirectory))
                .ImplementationType);

        Assert.Equal(
            typeof(ServiceAccountDirectory),
            Assert.Single(services, service => service.ServiceType == typeof(Contracts.Directories.IServiceAccountDirectory))
                .ImplementationType);

        Assert.False(Registers<ServiceLocationDirectory>(services));
        Assert.False(Registers<ServiceAccountDirectory>(services));
    }

    [Fact]
    public void The_deposit_lifecycle_is_registered_and_reads_the_billing_register_through_Contracts()
    {
        // WP-2.12, and the same boundary rule the search box follows: the deposit ledger asks
        // IBillDirectory what a bill still has outstanding before any of a deposit is applied, and
        // that interface is BILLING's to answer. A Customers registration of it would mean this
        // module referencing the assembly that holds the implementation.
        var services = ComposedModule();

        Assert.Equal(
            typeof(CustomerDepositService),
            Assert.Single(services, service => service.ServiceType == typeof(ICustomerDepositService)).ImplementationType);

        Assert.False(Registers<Contracts.Directories.IBillDirectory>(services));
    }

    [Fact]
    public void The_note_log_is_registered_and_reads_both_link_registers_through_Contracts()
    {
        // WP-2.13. The note log consumes TWO seams — IBillDirectory and the new IPaymentDirectory —
        // to confirm that a note filed against a bill or a payment names a real one of this
        // customer's. Both belong to the modules that own those registers; a Customers registration
        // of either would mean this module referencing the assembly that holds the implementation.
        var services = ComposedModule();

        Assert.Equal(
            typeof(CustomerNoteService),
            Assert.Single(services, service => service.ServiceType == typeof(ICustomerNoteService)).ImplementationType);

        Assert.False(Registers<Contracts.Directories.IBillDirectory>(services));
        Assert.False(Registers<Contracts.Directories.IPaymentDirectory>(services));
    }

    [Fact]
    public void The_search_box_is_registered_and_reads_the_meter_register_through_Contracts()
    {
        // WP-2.9. This is the first service in Customers that consumes another module's seam, so the
        // thing worth pinning is what it does NOT register: IMeterDirectory is Metering's to answer,
        // and a Customers registration of it would mean this module referencing the assembly that
        // holds the implementation — the dependency ARCHITECTURE.md's boundary rule forbids.
        var services = ComposedModule();

        Assert.Equal(
            typeof(CustomerSearchService),
            Assert.Single(services, service => service.ServiceType == typeof(ICustomerSearchService)).ImplementationType);

        Assert.DoesNotContain(services, service => service.ServiceType == typeof(Contracts.Directories.IMeterDirectory));
        Assert.False(Registers<CustomerSearchService>(services));
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
        Assert.True(Registers<IValidator<ChangeCustomerClassRequest>>(services));
        Assert.True(Registers<IValidator<ChangeCustomerStatusRequest>>(services));
        Assert.True(Registers<IValidator<MoveInRequest>>(services));
        Assert.True(Registers<IValidator<MoveOutRequest>>(services));
        Assert.True(Registers<IValidator<TransferServiceRequest>>(services));
        Assert.True(Registers<IValidator<ServiceLocationRequest>>(services));
        Assert.True(Registers<IValidator<OpenServiceAccountRequest>>(services));
        Assert.True(Registers<IValidator<ServiceAccountTransitionRequest>>(services));
        Assert.True(Registers<IValidator<LogNoteRequest>>(services));
        Assert.True(Registers<IValidator<CorrectNoteRequest>>(services));
        Assert.True(Registers<IValidator<PinNoteRequest>>(services));
    }

    [Theory]
    [InlineData(typeof(CustomersDemoSeeder))]
    [InlineData(typeof(ServiceAccountsDemoSeeder))]
    [InlineData(typeof(AccountTransitionsDemoSeeder))]
    [InlineData(typeof(CustomerNotesDemoSeeder))]
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
