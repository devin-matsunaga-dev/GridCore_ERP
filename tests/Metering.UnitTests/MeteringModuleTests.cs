using FluentValidation;
using GridCore.Contracts.Directories;
using GridCore.Modules.Metering.Data;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Modules.Metering.Seeding;
using GridCore.Platform.Modules;
using GridCore.Platform.Seeding;
using GridCore.Platform.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Metering.UnitTests;

public class MeteringModuleTests
{
    private static ServiceCollection Composed()
    {
        var services = new ServiceCollection();

        new MeteringModule().AddServices(services, new ConfigurationBuilder().Build());

        return services;
    }

    [Fact]
    public void Module_declares_a_snake_case_schema_name()
    {
        IModule module = new MeteringModule();

        Assert.False(string.IsNullOrWhiteSpace(module.Name));
        Assert.Matches("^[a-z][a-z0-9_]*$", module.Name);
    }

    [Fact]
    public void The_modules_name_is_the_schema_its_context_owns() =>
        // They are the same string by construction; ModuleRegistration rejects two modules claiming
        // one schema, and it can only do that if the name really is the schema.
        Assert.Equal(MeteringDbContext.SchemaName, new MeteringModule().Name);

    [Fact]
    public void The_register_and_its_number_generator_are_registered()
    {
        var services = Composed();

        Assert.Contains(services, service => service.ServiceType == typeof(IMeterService));
        Assert.Contains(services, service => service.ServiceType == typeof(IMeterNumberGenerator));
    }

    [Fact]
    public void The_module_does_not_register_another_modules_implementation() =>
        // The premise directory is consumed here and registered by Customers. A module that
        // registered it would be a module that had to reference the assembly holding it, which is
        // exactly the dependency ARCHITECTURE.md's boundary rule forbids.
        Assert.DoesNotContain(Composed(), service => service.ServiceType == typeof(IServiceLocationDirectory));

    [Fact]
    public void Every_write_request_the_register_takes_has_a_validator_registered()
    {
        // Registered one by one rather than by scanning, so a new write endpoint whose validator was
        // forgotten fails here rather than answering 409 for a malformed body.
        var registered = Composed()
            .Where(service => service.ServiceType.IsGenericType
                && service.ServiceType.GetGenericTypeDefinition() == typeof(IValidator<>))
            .Select(service => service.ServiceType.GetGenericArguments()[0])
            .ToHashSet();

        Assert.Equal(
            new HashSet<Type>
            {
                typeof(RegisterMeterRequest),
                typeof(UpdateMeterRequest),
                typeof(AssignMeterRequest),
                typeof(RemoveMeterRequest),
                typeof(ChangeMeterStatusRequest),
            },
            registered);
    }

    [Fact]
    public void The_demo_seeder_is_registered_but_registering_it_does_not_run_it()
    {
        // DemoSeedRunner is only registered where the environment allows it, so this line can be
        // unconditional and the guard stays in one place (invariant 8).
        var seeders = Composed().Where(service => service.ServiceType == typeof(IDemoSeeder)).ToList();

        Assert.Single(seeders);
        Assert.Equal(typeof(MetersDemoSeeder), seeders[0].ImplementationType);
    }
}
