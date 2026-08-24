using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.Seeding;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Data;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Customers.UnitTests.Seeding;

/// <summary>
/// The service accounts the demo world opens with. Development-only — the guard is the platform's.
/// What matters here is that the dataset is coherent: every pairing resolves, every account is in a
/// state its own machine can reach, and the four statuses a registry screen has to render all
/// appear.
/// </summary>
public class ServiceAccountsDemoSeederTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Both seeders in order, each in its own unit of work — exactly as the runner drives them, and
    /// the reason this one can query rows the other wrote.
    /// </summary>
    private static async Task SeedAsync(CustomersTestHost host)
    {
        await host.InScopeAsync<object?>(async services =>
        {
            var database = services.GetRequiredService<CustomersDbContext>();
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();

            await unitOfWork.ExecuteAsync(new CustomersDemoSeeder(database, new FakeClock(Now)).SeedAsync);
            await unitOfWork.ExecuteAsync(new ServiceAccountsDemoSeeder(database, new FakeClock(Now)).SeedAsync);

            return null;
        });
    }

    [Fact]
    public void The_seeder_name_is_the_dedupe_key_and_runs_after_the_registry()
    {
        var seeder = new ServiceAccountsDemoSeeder(null!, TimeProvider.System);

        // Renaming this seeds a second set of accounts on the next start. It is not a label.
        Assert.Equal("customers.service-accounts", seeder.Name);
        Assert.True(seeder.Order > new CustomersDemoSeeder(null!, TimeProvider.System).Order);
    }

    [Fact]
    public void Seeded_accounts_are_attributed_to_a_demo_colleague_who_holds_no_permissions()
    {
        var agent = ServiceAccountsDemoSeeder.Agent;

        Assert.StartsWith(DemoActor.IdPrefix, agent.UserId, StringComparison.Ordinal);
        Assert.False(agent.HasPermission("customers.write"));
    }

    [Fact]
    public async Task Seeding_joins_customers_to_premises_and_numbers_the_accounts_from_one()
    {
        using var host = new CustomersTestHost(new FakeClock(Now));

        await SeedAsync(host);

        await using var database = host.NewCustomersContext();

        var numbers = await database.ServiceAccounts
            .OrderBy(account => account.AccountNumber)
            .Select(account => account.AccountNumber)
            .ToListAsync();

        Assert.NotEmpty(numbers);

        // Starting at 1 with no gaps is what lets the first real account continue the series.
        Assert.Equal(
            Enumerable.Range(1, numbers.Count).Select(ordinal => RegistryNumbers.Format(RegistryNumbers.ServiceAccountPrefix, ordinal)),
            numbers);
    }

    [Fact]
    public async Task Every_seeded_account_points_at_a_customer_and_a_premise_that_exist()
    {
        using var host = new CustomersTestHost(new FakeClock(Now));

        await SeedAsync(host);

        await using var database = host.NewCustomersContext();

        var customers = await database.Customers.Select(customer => customer.Id).ToListAsync();
        var premises = await database.ServiceLocations.Select(location => location.Id).ToListAsync();
        var accounts = await database.ServiceAccounts.ToListAsync();

        Assert.All(accounts, account =>
        {
            Assert.Contains(account.CustomerId, customers);
            Assert.Contains(account.ServiceLocationId, premises);
        });
    }

    [Fact]
    public async Task The_demo_world_shows_all_four_statuses()
    {
        // Eight identical pills prove nothing about the design system. One of each is the point.
        using var host = new CustomersTestHost(new FakeClock(Now));

        await SeedAsync(host);

        await using var database = host.NewCustomersContext();

        var statuses = await database.ServiceAccounts.Select(account => account.Status).Distinct().ToListAsync();

        Assert.Equal(Enum.GetValues<ServiceAccountStatus>().Order(), statuses.Order());
    }

    [Fact]
    public async Task No_premise_is_served_by_two_open_accounts()
    {
        // The invariant the filtered unique index enforces, asserted against the seeded data — a
        // demo world that violates it would fail on Postgres and pass on the fast tier's model.
        using var host = new CustomersTestHost(new FakeClock(Now));

        await SeedAsync(host);

        await using var database = host.NewCustomersContext();

        var open = await database.ServiceAccounts
            .Where(account => account.Status != ServiceAccountStatus.Closed)
            .Select(account => account.ServiceLocationId)
            .ToListAsync();

        Assert.Equal(open.Count, open.Distinct().Count());
    }

    [Fact]
    public async Task Every_seeded_account_carries_the_history_its_transitions_produced()
    {
        using var host = new CustomersTestHost(new FakeClock(Now));

        await SeedAsync(host);

        await using var database = host.NewCustomersContext();

        var accounts = await database.ServiceAccounts.Include(account => account.History).ToListAsync();

        Assert.All(accounts, account =>
        {
            // The opening line at least, and the last line is where the account now stands.
            Assert.NotEmpty(account.History);
            Assert.Null(account.History.OrderBy(entry => entry.Id).First().FromStatus);
            Assert.Equal(account.Status, account.History.OrderBy(entry => entry.Id).Last().ToStatus);
            Assert.All(account.History, entry => Assert.StartsWith(DemoActor.IdPrefix, entry.ActorId, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task A_customer_holding_two_accounts_is_in_the_demo_world()
    {
        // The customer 360 page (WP-1.5) has to render more than one account, so the data has to
        // contain more than one.
        using var host = new CustomersTestHost(new FakeClock(Now));

        await SeedAsync(host);

        await using var database = host.NewCustomersContext();

        var byCustomer = await database.ServiceAccounts
            .GroupBy(account => account.CustomerId)
            .Select(group => group.Count())
            .ToListAsync();

        Assert.Contains(byCustomer, count => count > 1);
    }

    [Fact]
    public async Task Seeding_without_the_registry_it_depends_on_fails_loudly()
    {
        // The failure path: a demo world that quietly skips half its accounts because a place name
        // was edited is worse than one that refuses to start and names the missing row.
        using var host = new CustomersTestHost(new FakeClock(Now));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.InScopeAsync<object?>(async services =>
            {
                var seeder = new ServiceAccountsDemoSeeder(services.GetRequiredService<CustomersDbContext>(), new FakeClock(Now));

                await services.GetRequiredService<IUnitOfWork>().ExecuteAsync(seeder.SeedAsync);

                return null;
            }));

        Assert.Contains("C-000001", failure.Message, StringComparison.Ordinal);
    }
}
