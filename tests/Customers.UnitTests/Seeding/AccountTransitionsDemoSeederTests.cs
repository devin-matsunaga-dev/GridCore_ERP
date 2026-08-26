using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Transitions;
using GridCore.Modules.Customers.Seeding;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Data;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Customers.UnitTests.Seeding;

/// <summary>
/// The transition register the demo world opens with. Development-only — the guard is the
/// platform's. What matters here is that the one row it writes is <i>true</i>: it describes a
/// closure the account seeder actually performed, against an account that really is closed.
/// </summary>
public class AccountTransitionsDemoSeederTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The three seeders in order, each in its own unit of work — exactly as the runner drives them,
    /// and the reason this one can query rows the others wrote.
    /// </summary>
    private static Task SeedAsync(CustomersTestHost host) =>
        host.InScopeAsync<object?>(async services =>
        {
            var database = services.GetRequiredService<CustomersDbContext>();
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();

            await unitOfWork.ExecuteAsync(new CustomersDemoSeeder(database, new FakeClock(Now)).SeedAsync);
            await unitOfWork.ExecuteAsync(new ServiceAccountsDemoSeeder(database, new FakeClock(Now)).SeedAsync);
            await unitOfWork.ExecuteAsync(new AccountTransitionsDemoSeeder(database, new FakeClock(Now)).SeedAsync);

            return null;
        });

    [Fact]
    public void The_seeder_name_is_the_dedupe_key_and_runs_after_the_accounts()
    {
        var seeder = new AccountTransitionsDemoSeeder(null!, TimeProvider.System);

        // Renaming this seeds a second set of transitions on the next start. It is not a label.
        Assert.Equal("customers.account-transitions", seeder.Name);
        Assert.True(seeder.Order > new ServiceAccountsDemoSeeder(null!, TimeProvider.System).Order);
    }

    [Fact]
    public async Task The_one_seeded_transition_describes_a_closure_that_actually_happened()
    {
        // The whole point of seeding only one. The demo world's Suspended customer and its Commercial
        // customers were REGISTERED that way rather than moved there, so a row claiming otherwise
        // would be inventing a change nobody made — the opposite of what a register is for.
        using var host = new CustomersTestHost(new FakeClock(Now));
        await SeedAsync(host);

        await using var database = host.NewCustomersContext();

        var transition = Assert.Single(await database.AccountTransitions.ToListAsync());

        Assert.Equal(AccountTransitionKind.MovedOut, transition.Kind);
        Assert.Equal(TransitionReasonCode.EndOfTenancy, transition.ReasonCode);
        Assert.Equal("A-000007", transition.FromValue);
        Assert.Null(transition.ToValue);

        var account = await database.ServiceAccounts.SingleAsync(row => row.Id == transition.FromServiceAccountId);

        Assert.Equal("A-000007", account.AccountNumber);
        Assert.Equal(ServiceAccountStatus.Closed, account.Status);
        Assert.Equal(account.CustomerId, transition.CustomerId);
    }

    [Fact]
    public async Task The_seeded_transition_is_effective_on_a_day_it_was_not_recorded_on()
    {
        // So the demo world opens with the distinction the tab exists to make readable: what was
        // done, and when it applies from. A seeded row where the two agreed would hide it.
        using var host = new CustomersTestHost(new FakeClock(Now));
        await SeedAsync(host);

        await using var database = host.NewCustomersContext();
        var transition = Assert.Single(await database.AccountTransitions.ToListAsync());

        Assert.Equal(Now, transition.RecordedAt);
        Assert.NotEqual(DateOnly.FromDateTime(Now.UtcDateTime), transition.EffectiveOn);
    }

    [Fact]
    public async Task The_seeded_transition_is_attributed_to_the_demo_colleague_and_marked_as_demo()
    {
        // The demo: prefix is what stops a seeded register row being mistaken for one a real agent
        // made — the rule every seeder in this module follows.
        using var host = new CustomersTestHost(new FakeClock(Now));
        await SeedAsync(host);

        await using var database = host.NewCustomersContext();
        var transition = Assert.Single(await database.AccountTransitions.ToListAsync());

        Assert.StartsWith(DemoActor.IdPrefix, transition.ActorId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_refuses_to_seed_against_an_account_the_accounts_seeder_never_wrote()
    {
        // Failure path: a demo world that quietly skipped its one transition because a pairing was
        // edited is worse than one that refuses to start and says which row is missing.
        using var host = new CustomersTestHost(new FakeClock(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.InScopeAsync<object?>(async services =>
        {
            var database = services.GetRequiredService<CustomersDbContext>();
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();

            // Deliberately WITHOUT ServiceAccountsDemoSeeder, which is what an edited pairing amounts
            // to as far as this seeder is concerned.
            await unitOfWork.ExecuteAsync(new CustomersDemoSeeder(database, new FakeClock(Now)).SeedAsync);
            await unitOfWork.ExecuteAsync(new AccountTransitionsDemoSeeder(database, new FakeClock(Now)).SeedAsync);

            return null;
        }));
    }
}
