using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// The intake wizard's single commit against real Postgres and the shipped composition (WP-2.8).
/// </summary>
/// <remarks>
/// The fast tier proves the rules and the rollback on SQLite. What only a container can show is the
/// claim the wizard is built on: one transaction spanning three tables in <c>customers</c> and the
/// audit and outbox tables in <c>platform</c>, on one connection, through four nested units of work
/// — and that a refusal at the last step really does leave the first three writes with nowhere to
/// commit to.
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CustomerIntakeTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    private static CustomerIntakeInput AnIntake(IntakePremise premise, decimal deposit = 0m, bool startService = false) =>
        new(
            "Reyes Family Residence",
            CustomerClass.Residential,
            premise,
            "Ana Reyes",
            "ana.reyes@example.com",
            "+1-670-532-0199",
            deposit,
            startService,
            "New connection");

    private static ServiceLocationInput APremise() =>
        new(Address.Create("77 As Nieves Road", "Songsong", "Rota", "MP"), "Meter on the north wall");

    [Fact]
    public async Task An_intake_commits_the_customer_the_premise_the_account_and_their_audit_trail_together()
    {
        CustomerRegistration? registration = null;

        await using (var scope = fixture.CreateScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var customers = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();

            // The intake nests inside this transaction rather than opening one of its own, so the
            // assertions run on the pending state — every write of the wizard still one atomic unit.
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(async token =>
            {
                registration = await scope.ServiceProvider
                    .GetRequiredService<ICustomerRegistrationService>()
                    .RegisterAsync(AnIntake(new IntakePremise(APremise()), deposit: 75.00m, startService: true), token);

                // Four tables in the customers schema — the deposit ledger joined them in WP-2.12,
                // which is what makes the collected figure a row somebody can reconcile rather than
                // a number written onto the customer.
                Assert.Single(customers.ChangeTracker.Entries<Customer>());
                Assert.Single(customers.ChangeTracker.Entries<ServiceLocation>());
                Assert.Single(customers.ChangeTracker.Entries<ServiceAccount>());
                Assert.Single(customers.ChangeTracker.Entries<DepositEntry>());

                // …and, in the platform schema, an audit entry per write plus the deposit's own,
                // with an outbox row for each fact the registries published. The fifth outbox row is
                // CustomerDepositCollected, which Finance posts the liability from — before WP-2.12
                // the intake recorded a deposit the ledger never heard about.
                Assert.Equal(5, platform.ChangeTracker.Entries<AuditEntry>().Count());
                Assert.Equal(5, platform.ChangeTracker.Entries<OutboxMessage>().Count());
            });
        }

        Assert.NotNull(registration);

        // Read on a fresh scope: only what actually committed is visible.
        await using var read = fixture.CreateScope();

        var stored = await read.ServiceProvider.GetRequiredService<CustomersDbContext>()
            .ServiceAccounts.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == registration.Account.Id);

        Assert.Equal(ServiceAccountStatus.Active, stored.Status);
        Assert.Equal(registration.Customer.Id, stored.CustomerId);
        Assert.Equal(registration.Location.Id, stored.ServiceLocationId);

        var customer = await read.ServiceProvider.GetRequiredService<CustomersDbContext>()
            .Customers.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == registration.Customer.Id);

        Assert.Equal(75.00m, customer.DepositHeld);

        var deposit = await read.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .AuditEntries.AsNoTracking()
            .SingleAsync(entry => entry.Action == AuditActions.CustomerDepositCollected);

        Assert.Equal(registration.Customer.Id.ToString(), deposit.EntityId);
    }

    [Fact]
    public async Task An_intake_refused_at_its_last_step_leaves_no_customer_no_premise_and_no_account()
    {
        // The case the single commit exists for, on a real transaction: the customer and the premise
        // have already been written when the account registry refuses the premise as occupied, and
        // neither survives — along with their audit entries and their outbox rows in the other schema.
        await using var first = fixture.CreateScope();

        var taken = await first.ServiceProvider
            .GetRequiredService<ICustomerRegistrationService>()
            .RegisterAsync(AnIntake(new IntakePremise(APremise())));

        var before = await CountAsync();

        await using var second = fixture.CreateScope();

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            second.ServiceProvider
                .GetRequiredService<ICustomerRegistrationService>()
                .RegisterAsync(AnIntake(new IntakePremise(ServiceLocationId: taken.Location.Id))));

        Assert.Equal(before, await CountAsync());
    }

    [Fact]
    public async Task The_migrated_database_can_assess_a_deposit_without_a_seeder()
    {
        // Invariant 8 for this WP's reference data: the schedule is in the schema, so a database
        // that has only been migrated can quote a figure — and Respawn wiping demo data between
        // tests does not take it away.
        await using var scope = fixture.CreateScope();

        var schedule = await scope.ServiceProvider.GetRequiredService<IDepositRuleService>().ListAsync();

        Assert.Equal(
            DepositRules.All.Select(rule => rule.CustomerClass).Order(),
            schedule.Select(assessment => assessment.CustomerClass).Order());

        var residential = await scope.ServiceProvider
            .GetRequiredService<IDepositRuleService>()
            .AssessAsync(CustomerClass.Residential);

        Assert.Equal(
            DepositRules.All.Single(rule => rule.CustomerClass == CustomerClass.Residential).Amount,
            residential.Amount);
    }

    private async Task<(int Customers, int Locations, int Accounts, int Audits)> CountAsync()
    {
        await using var scope = fixture.CreateScope();

        var customers = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        return (
            await customers.Customers.CountAsync(),
            await customers.ServiceLocations.CountAsync(),
            await customers.ServiceAccounts.CountAsync(),
            await platform.AuditEntries.CountAsync());
    }
}
