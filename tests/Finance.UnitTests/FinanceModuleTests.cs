using FluentValidation;
using GridCore.Contracts.Directories;
using GridCore.Modules.Finance.Data;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.Journal;
using GridCore.Modules.Finance.Features.Reports;
using GridCore.Modules.Finance.Features.Shared;
using GridCore.Platform.Modules;
using GridCore.Platform.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Finance.UnitTests;

public class FinanceModuleTests
{
    private static ServiceCollection Composed()
    {
        var services = new ServiceCollection();

        new FinanceModule().AddServices(services, new ConfigurationBuilder().Build());

        return services;
    }

    [Fact]
    public void Module_declares_a_snake_case_schema_name()
    {
        IModule module = new FinanceModule();

        Assert.False(string.IsNullOrWhiteSpace(module.Name));
        Assert.Matches("^[a-z][a-z0-9_]*$", module.Name);
    }

    [Fact]
    public void The_modules_name_is_the_schema_its_context_owns() =>
        // They are the same string by construction; ModuleRegistration rejects two modules claiming
        // one schema, and it can only do that if the name really is the schema.
        Assert.Equal(FinanceDbContext.SchemaName, new FinanceModule().Name);

    [Fact]
    public void The_ledger_its_reports_and_its_number_generator_are_registered()
    {
        var services = Composed();

        Assert.Contains(services, service => service.ServiceType == typeof(IJournalService));
        Assert.Contains(services, service => service.ServiceType == typeof(IFinanceReportService));
        Assert.Contains(services, service => service.ServiceType == typeof(IChartOfAccountsService));
        Assert.Contains(services, service => service.ServiceType == typeof(IJournalEntryNumberGenerator));
    }

    [Theory]
    [InlineData(typeof(IBillDirectory))]
    [InlineData(typeof(IServiceAccountDirectory))]
    [InlineData(typeof(IServiceLocationDirectory))]
    [InlineData(typeof(IMeterReadingDirectory))]
    public void Finance_neither_registers_nor_consumes_another_modules_read_seam(Type directory) =>
        // "Finance is downstream of everyone" in the shape a DI container can be asked about. It
        // registers none of these because it owns none of the data, and it consumes none because
        // everything an entry needs is on the event that caused it — which is why the fast-tier test
        // host for this module fakes nothing at all, unlike Billing's and Payments'.
        Assert.DoesNotContain(Composed(), service => service.ServiceType == directory);

    [Fact]
    public void The_module_registers_no_edge_validators() =>
        // Nothing is posted from the wire, so there is no request body to validate. A validator
        // appearing here would mean a write endpoint had appeared with it.
        Assert.DoesNotContain(
            Composed(),
            service => service.ServiceType.IsGenericType
                && service.ServiceType.GetGenericTypeDefinition() == typeof(IValidator<>));

    [Fact]
    public void The_module_seeds_no_demo_data()
    {
        // Deliberate, and the one place a reader will look for why. Seeded journal entries would be
        // Finance's own account of a demo world it never heard about: BillsDemoSeeder writes bills
        // straight to Billing's tables and publishes nothing (a seeder adds entities and never
        // publishes), so the ledger has no events behind it to post. Inventing entries to match
        // would put figures in the trial balance that no upstream fact explains — the one thing a
        // ledger must never do. WP-2.7's end-to-end walk raises real events and the ledger fills
        // itself from them. See STATUS.md.
        Assert.DoesNotContain(Composed(), service => service.ServiceType == typeof(IDemoSeeder));
    }
}
