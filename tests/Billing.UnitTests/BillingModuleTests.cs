using FluentValidation;
using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Delinquency;
using GridCore.Modules.Billing.Features.Fees;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Modules.Billing.Seeding;
using GridCore.Platform.Messaging;
using GridCore.Platform.Modules;
using GridCore.Platform.Seeding;
using GridCore.Platform.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Billing.UnitTests;

public class BillingModuleTests
{
    private static ServiceCollection Composed()
    {
        var services = new ServiceCollection();

        new BillingModule().AddServices(services, new ConfigurationBuilder().Build());

        return services;
    }

    [Fact]
    public void Module_declares_a_snake_case_schema_name()
    {
        IModule module = new BillingModule();

        Assert.False(string.IsNullOrWhiteSpace(module.Name));
        Assert.Matches("^[a-z][a-z0-9_]*$", module.Name);
    }

    [Fact]
    public void The_modules_name_is_the_schema_its_context_owns() =>
        // They are the same string by construction; ModuleRegistration rejects two modules claiming
        // one schema, and it can only do that if the name really is the schema.
        Assert.Equal(BillingDbContext.SchemaName, new BillingModule().Name);

    [Fact]
    public void The_register_and_its_number_generator_are_registered()
    {
        var services = Composed();

        Assert.Contains(services, service => service.ServiceType == typeof(IBillService));
        Assert.Contains(services, service => service.ServiceType == typeof(IRatePlanService));
        Assert.Contains(services, service => service.ServiceType == typeof(IBillNumberGenerator));
    }

    [Fact]
    public void This_module_registers_the_bill_directory_because_it_owns_the_bills() =>
        // The other side of the boundary rule below: Payments consumes IBillDirectory and may
        // neither reference this assembly nor read the billing schema, so the module that owns the
        // data registers the implementation.
        Assert.Contains(Composed(), service => service.ServiceType == typeof(IBillDirectory));

    [Fact]
    public void The_module_consumes_everything_that_settles_its_bills()
    {
        // Billing's first consumer (WP-2.5). It published BillIssued from WP-2.3 and BillAdjusted
        // from WP-2.4; this is the other direction. WP-2.12 added the second: a customer's security
        // deposit put against a bill settles it the same way cash does, and both arrive as facts
        // from the module that moved the money. Registered on the service collection so
        // AddGridCoreMessaging can read it back — no assembly scanning, so the composition stays
        // greppable.
        var consumers = Composed()
            .Where(service => service.ServiceType == typeof(EventConsumerDescriptor))
            .Select(service => (service.ImplementationInstance as EventConsumerDescriptor)?.ConsumerType)
            .ToList();

        Assert.Equal([typeof(PaymentApprovedConsumer), typeof(CustomerDepositAppliedConsumer)], consumers);
    }

    [Theory]
    [InlineData(typeof(IServiceAccountDirectory))]
    [InlineData(typeof(IMeterReadingDirectory))]
    public void The_module_does_not_register_another_modules_implementation(Type directory) =>
        // Both are consumed here and registered by the modules that own the data — Customers and
        // Metering. A module that registered one would be a module that had to reference the
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

        Assert.Equal(
            new HashSet<Type>
            {
                typeof(RunBillingRequest),
                typeof(IssueBillRequest),
                typeof(CancelBillRequest),
                typeof(AdjustBillRequest),
                typeof(OverdueReviewRequest),
                typeof(AssignRatePlanRequest),
                typeof(RaiseChargeRequest),
                typeof(CancelChargeRequest),
                typeof(BillChargeRequest),
                typeof(LateChargeRunRequest),
            },
            registered);
    }

    [Fact]
    public void The_demo_seeder_is_registered_but_registering_it_does_not_run_it()
    {
        // DemoSeedRunner is only registered where the environment allows it, so this line can be
        // unconditional and the guard stays in one place (invariant 8).
        var seeders = Composed()
            .Where(service => service.ServiceType == typeof(IDemoSeeder))
            .Select(service => service.ImplementationType)
            .ToList();

        Assert.Equal([typeof(BillsDemoSeeder)], seeders);
    }

    [Fact]
    public void The_bills_seeder_runs_after_the_readings_it_bills() =>
        // Order is what lets a seeder query rows an earlier one committed — each runs in its own
        // unit of work, so a bills seeder that ran before the reading register would find no cycles
        // at all. 700 is metering.readings; this is 800.
        Assert.True(new BillsDemoSeeder(null!, null!, null!, TimeProvider.System).Order > 700);
}
