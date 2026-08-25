using FluentValidation;
using GridCore.Contracts.Directories;
using GridCore.Contracts.Providers;
using GridCore.Modules.Metering.Data;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Modules.Metering.Seeding;
using GridCore.Modules.Metering.Simulation;
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
        Assert.Contains(services, service => service.ServiceType == typeof(IMeterReadingService));
    }

    [Fact]
    public void The_reading_register_is_published_to_other_modules_through_Contracts()
    {
        // WP-2.3's seam. Billing bills from readings and may not read this schema, so Metering — the
        // only module that knows both halves — registers the implementation against the Contracts
        // interface, exactly as Customers does for the premise directory.
        var directory = Assert.Single(Composed(), service => service.ServiceType == typeof(IMeterReadingDirectory));

        Assert.Equal(typeof(MeterReadingDirectory), directory.ImplementationType);

        // And never as the concrete type: a caller that could resolve MeterReadingDirectory would be
        // a caller holding this module's EF context by another name.
        Assert.DoesNotContain(Composed(), service => service.ServiceType == typeof(MeterReadingDirectory));
    }

    [Fact]
    public void The_meter_register_is_published_to_other_modules_through_Contracts()
    {
        // WP-2.9's seam, and the fifth in GridCore. Customers turns a quoted meter number into the
        // premise it measures and may not read this schema; Metering could not finish the job either,
        // because "whose meter is this" is a question about the customers schema. So the boundary
        // sits in the middle of the resolution, with a directory on this side of it.
        var directory = Assert.Single(Composed(), service => service.ServiceType == typeof(IMeterDirectory));

        Assert.Equal(typeof(MeterDirectory), directory.ImplementationType);
        Assert.DoesNotContain(Composed(), service => service.ServiceType == typeof(MeterDirectory));
    }

    [Fact]
    public void The_meter_simulator_is_registered_only_behind_the_provider_interface()
    {
        // ARCHITECTURE.md's module table gives Metering the meter simulator, so unlike the premise
        // directory this module DOES register it — but only against the Contracts interface. A
        // service registered as the concrete simulator would be one domain code could resolve and
        // call by name, which is invariant 6 gone.
        var services = Composed();

        var provider = Assert.Single(services, service => service.ServiceType == typeof(IMeterReadingProvider));

        Assert.Equal(typeof(SimulatedMeterReadingProvider), provider.ImplementationType);
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(SimulatedMeterReadingProvider));
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
                typeof(RecordMeterReadingRequest),
                typeof(RunReadingCycleRequest),
            },
            registered);
    }

    [Fact]
    public void The_demo_seeders_are_registered_but_registering_them_does_not_run_them()
    {
        // DemoSeedRunner is only registered where the environment allows it, so these lines can be
        // unconditional and the guard stays in one place (invariant 8).
        var seeders = Composed()
            .Where(service => service.ServiceType == typeof(IDemoSeeder))
            .Select(service => service.ImplementationType)
            .ToList();

        Assert.Equal([typeof(MetersDemoSeeder), typeof(MeterReadingsDemoSeeder)], seeders);
    }

    [Fact]
    public void The_readings_seeder_runs_after_the_meters_it_reads() =>
        // Order is what lets the later seeder query rows the earlier one committed — each runs in
        // its own unit of work, so a readings seeder that ran first would find no meters at all.
        Assert.True(new MeterReadingsDemoSeeder(null!, null!, TimeProvider.System).Order
            > new MetersDemoSeeder(null!, null!, TimeProvider.System).Order);
}
