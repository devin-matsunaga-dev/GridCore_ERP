using FluentValidation;
using GridCore.Contracts.Directories;
using GridCore.Contracts.Providers;
using GridCore.Modules.Payments.Data;
using GridCore.Modules.Payments.Features.Payments;
using GridCore.Modules.Payments.Features.Shared;
using GridCore.Modules.Payments.Simulation;
using GridCore.Platform.Modules;
using GridCore.Platform.Seeding;
using GridCore.Platform.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Payments.UnitTests;

public class PaymentsModuleTests
{
    private static ServiceCollection Composed()
    {
        var services = new ServiceCollection();

        new PaymentsModule().AddServices(services, new ConfigurationBuilder().Build());

        return services;
    }

    [Fact]
    public void Module_declares_a_snake_case_schema_name()
    {
        IModule module = new PaymentsModule();

        Assert.False(string.IsNullOrWhiteSpace(module.Name));
        Assert.Matches("^[a-z][a-z0-9_]*$", module.Name);
    }

    [Fact]
    public void The_modules_name_is_the_schema_its_context_owns() =>
        // They are the same string by construction; ModuleRegistration rejects two modules claiming
        // one schema, and it can only do that if the name really is the schema.
        Assert.Equal(PaymentsDbContext.SchemaName, new PaymentsModule().Name);

    [Fact]
    public void The_register_and_its_number_generator_are_registered()
    {
        var services = Composed();

        Assert.Contains(services, service => service.ServiceType == typeof(IPaymentService));
        Assert.Contains(services, service => service.ServiceType == typeof(IPaymentNumberGenerator));
    }

    [Fact]
    public void The_payment_provider_is_registered_only_behind_the_contracts_interface()
    {
        // INVARIANT 6, in the shape a DI container can be asked about. The sandbox is reachable as
        // IPaymentProvider and by no other name, which is what lets a production deployment swap in
        // a real gateway by changing one line — and what stops domain code taking a dependency on a
        // simulator. The same assertion WP-2.2 makes about the meter reading provider.
        var services = Composed();

        Assert.Contains(services, service => service.ServiceType == typeof(IPaymentProvider));
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(SimulatedPaymentProvider));
    }

    [Fact]
    public void The_register_THIS_module_owns_is_published_through_Contracts()
    {
        // WP-2.13's seam, and the mirror of the rule below: Payments owns the payments, so Payments
        // is the one place that knows both halves of IPaymentDirectory and the only place that may
        // register it. Always against the Contracts interface, never the concrete type, or a consumer
        // could hold this module's EF context by another name.
        var services = Composed();

        Assert.Equal(
            typeof(PaymentDirectory),
            Assert.Single(services, service => service.ServiceType == typeof(IPaymentDirectory)).ImplementationType);

        Assert.DoesNotContain(services, service => service.ServiceType == typeof(PaymentDirectory));
    }

    [Theory]
    [InlineData(typeof(IBillDirectory))]
    [InlineData(typeof(IServiceAccountDirectory))]
    public void The_module_does_not_register_another_modules_implementation(Type directory) =>
        // Both are consumed here and registered by the modules that own the data — Billing and
        // Customers. A module that registered one would be a module that had to reference the
        // assembly holding it, which is exactly the dependency ARCHITECTURE.md's boundary rule
        // forbids.
        Assert.DoesNotContain(Composed(), service => service.ServiceType == directory);

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

        Assert.Equal(new HashSet<Type> { typeof(TakePaymentRequest) }, registered);
    }

    [Fact]
    public void The_module_seeds_no_demo_data()
    {
        // Deliberate, and the one place a reader will look for why. A seeded payment would either
        // publish PaymentApproved — making the demo world's bills depend on broker timing, which no
        // other seeder does — or not publish, and leave settled payments beside bills that still
        // say they are owed. WP-2.7's end-to-end walk of the revenue cycle is where paid bills
        // belong. See STATUS.md.
        Assert.DoesNotContain(Composed(), service => service.ServiceType == typeof(IDemoSeeder));
    }
}
