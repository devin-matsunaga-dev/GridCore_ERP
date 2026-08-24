using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.Seeding;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Data;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Customers.UnitTests.Seeding;

/// <summary>
/// The demo world this module contributes. Development-only — the guard is the platform's and is
/// tested there; what matters here is that the dataset is coherent and that the seeder plays by the
/// runner's rules.
/// </summary>
public class CustomersDemoSeederTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static async Task<(IReadOnlyList<Customer> Customers, IReadOnlyList<string> Codes)> SeededAsync(CustomersTestHost host)
    {
        await using var context = host.NewCustomersContext();

        return (
            await context.Customers.OrderBy(customer => customer.AccountNumber).ToListAsync(),
            await context.ServiceLocations.OrderBy(location => location.LocationCode).Select(location => location.LocationCode).ToListAsync());
    }

    private static async Task SeedAsync(CustomersTestHost host)
    {
        // Exactly as DemoSeedRunner drives it: inside a unit of work, which is what saves the rows.
        await host.InScopeAsync<object?>(async services =>
        {
            var seeder = new CustomersDemoSeeder(services.GetRequiredService<CustomersDbContext>(), new FakeClock(Now));

            await services.GetRequiredService<IUnitOfWork>().ExecuteAsync(seeder.SeedAsync);

            return null;
        });
    }

    [Fact]
    public void The_seeder_name_is_the_dedupe_key_and_runs_after_the_platform_queue()
    {
        var seeder = new CustomersDemoSeeder(null!, TimeProvider.System);

        // Renaming this seeds a second copy of the registry on the next start. It is not a label.
        Assert.Equal("customers.registry", seeder.Name);
        Assert.True(seeder.Order > 100);
    }

    [Fact]
    public async Task Seeding_fills_the_registry_with_customers_and_premises()
    {
        using var host = new CustomersTestHost(new FakeClock(Now));

        await SeedAsync(host);

        var (customers, codes) = await SeededAsync(host);

        Assert.NotEmpty(customers);
        Assert.NotEmpty(codes);

        // The series starts at one and has no gaps, so a customer registered after the demo world
        // continues it rather than colliding with it.
        Assert.Equal(
            Enumerable.Range(1, customers.Count).Select(ordinal => RegistryNumbers.Format(CustomerNumbers.CustomerPrefix, ordinal)),
            customers.Select(customer => customer.AccountNumber));

        Assert.Equal(
            Enumerable.Range(1, codes.Count).Select(ordinal => RegistryNumbers.Format(CustomerNumbers.ServiceLocationPrefix, ordinal)),
            codes);
    }

    [Fact]
    public async Task A_customer_registered_after_the_demo_world_continues_its_series()
    {
        using var host = new CustomersTestHost(new FakeClock(Now));

        await SeedAsync(host);

        var (seeded, _) = await SeededAsync(host);

        var registered = await host.WithCustomersAsync(customers =>
            customers.RegisterAsync(new RegisterCustomerInput("Walk-in registration", CustomerClass.Residential)));

        Assert.Equal(RegistryNumbers.Format(CustomerNumbers.CustomerPrefix, seeded.Count + 1), registered.AccountNumber);
    }

    [Fact]
    public async Task Every_seeded_premise_is_on_one_of_the_three_islands()
    {
        // The demo utility is Rota Utilities. Rota, Saipan and Tinian are the three main Northern
        // Mariana Islands, and a real place name is what makes a demonstration screen believable
        // where an invented district reads as filler.
        using var host = new CustomersTestHost(new FakeClock(Now));

        await SeedAsync(host);

        await using var context = host.NewCustomersContext();

        var regions = await context.ServiceLocations.Select(location => location.Address.Region).Distinct().ToListAsync();

        Assert.Equal(CustomersDemoSeeder.Islands.Order(), regions.Order());
        Assert.All(
            await context.ServiceLocations.ToListAsync(),
            location => Assert.Equal(CustomersDemoSeeder.Country, location.Address.Country));
    }

    [Fact]
    public async Task The_seeded_world_shows_more_than_one_status_and_more_than_one_class()
    {
        // A registry where every row is an active residential customer demonstrates neither the
        // status pills nor the class filter.
        using var host = new CustomersTestHost(new FakeClock(Now));

        await SeedAsync(host);

        var (customers, _) = await SeededAsync(host);

        Assert.True(customers.Select(customer => customer.Status).Distinct().Count() > 1);
        Assert.True(customers.Select(customer => customer.Class).Distinct().Count() > 1);
    }

    [Fact]
    public async Task The_seeder_writes_nothing_by_itself()
    {
        // The runner's unit of work saves the rows and the "already seeded" record in one
        // transaction. A seeder that saved for itself could leave a half-seeded world behind.
        using var host = new CustomersTestHost(new FakeClock(Now));

        await host.InScopeAsync<object?>(async services =>
        {
            var context = services.GetRequiredService<CustomersDbContext>();

            await new CustomersDemoSeeder(context, new FakeClock(Now)).SeedAsync(CancellationToken.None);

            return null;
        });

        await using var fresh = host.NewCustomersContext();

        Assert.Empty(await fresh.Customers.ToListAsync());
    }
}
