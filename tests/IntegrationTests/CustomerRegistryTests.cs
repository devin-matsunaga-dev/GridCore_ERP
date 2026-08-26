using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// The customer registry against real Postgres and the shipped composition. The fast tier proves
/// the rules, the numbering and the rollback on SQLite; what a container adds is that one
/// transaction really does span two schemas on one connection — the customer row in
/// <c>customers</c>, its audit entry and its outbox row in <c>platform</c> — which is the whole
/// claim invariants 1 and 2 rest on and the one thing SQLite cannot be asked about.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CustomerRegistryTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Registering_a_customer_commits_the_row_its_audit_entry_and_its_event_together()
    {
        Customer? customer = null;

        await using (var scope = fixture.CreateScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            // The registration joins this transaction rather than opening one of its own (nesting
            // is what IUnitOfWork does), so the assertions below run on the pending state — before
            // the commit, while all three writes are still one atomic unit.
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(async token =>
            {
                customer = await scope.ServiceProvider
                    .GetRequiredService<ICustomerService>()
                    .RegisterAsync(
                        new RegisterCustomerInput(
                            "Songsong Village Market",
                            CustomerClass.Commercial,
                            "Elena Manglona",
                            "accounts@songsongmarket.example.com"),
                        token);

                // Both in the platform schema, both pending in the same transaction as the customer
                // row in the customers schema. The event is a database row here, not a message on a
                // broker — that is what makes the outbox transactional rather than merely a table.
                Assert.Single(platform.ChangeTracker.Entries<AuditEntry>());
                Assert.Single(platform.ChangeTracker.Entries<OutboxMessage>());
            });
        }

        Assert.NotNull(customer);

        // Read on a fresh scope: only what actually committed is visible.
        await using var read = fixture.CreateScope();

        var stored = await read.ServiceProvider.GetRequiredService<CustomersDbContext>()
            .Customers.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == customer.Id);

        // Registering takes no money since WP-2.12: every cent held has a ledger entry explaining
        // it, so a customer starts at zero and the deposit lifecycle is what moves them off it.
        Assert.Equal(0m, stored.DepositHeld);
        Assert.Equal(CustomerStatus.Prospect, stored.Status);

        var entry = await read.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .AuditEntries.AsNoTracking()
            .SingleAsync(candidate => candidate.EntityId == customer.Id.ToString());

        Assert.Equal(AuditActions.CustomerCreated, entry.Action);
        Assert.Equal(AuditEntityTypes.Customer, entry.EntityType);
    }

    [Fact]
    public async Task A_registration_that_fails_leaves_no_row_no_audit_entry_and_no_event()
    {
        // Failure path across two schemas: the aggregate throws inside the unit of work, so the
        // customers row, the platform audit entry and the platform outbox row all roll back on one
        // real transaction — the case a single-schema test can never actually exercise.
        var before = await CountAsync();

        await using (var scope = fixture.CreateScope())
        {
            await Assert.ThrowsAsync<RegistryValidationException>(() =>
                scope.ServiceProvider.GetRequiredService<ICustomerService>()
                    .RegisterAsync(new RegisterCustomerInput(" ", CustomerClass.Residential)));
        }

        Assert.Equal(before, await CountAsync());
    }

    [Fact]
    public async Task Registering_a_premise_stores_its_address_in_the_customers_schema()
    {
        ServiceLocation location;

        await using (var scope = fixture.CreateScope())
        {
            location = await scope.ServiceProvider
                .GetRequiredService<IServiceLocationService>()
                .RegisterAsync(new ServiceLocationInput(
                    Address.Create("22 Beach Road", "Garapan", "Saipan", "MP", "Unit 4", "96950"),
                    "Hotel main intake"));
        }

        await using var read = fixture.CreateScope();

        var stored = await read.ServiceProvider.GetRequiredService<CustomersDbContext>()
            .ServiceLocations.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == location.Id);

        // Owned type, inlined columns: the address survives Postgres, not just the SQLite model.
        Assert.Equal("Unit 4", stored.Address.Line2);
        Assert.Equal("Saipan", stored.Address.Region);
        Assert.Equal("22 Beach Road, Unit 4, Garapan, Saipan, 96950", stored.Address.OneLine);
    }

    [Fact]
    public async Task Account_numbers_continue_across_requests_and_cannot_repeat()
    {
        var issued = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            await using var scope = fixture.CreateScope();

            issued.Add((await scope.ServiceProvider.GetRequiredService<ICustomerService>()
                .RegisterAsync(new RegisterCustomerInput($"Customer {i}", CustomerClass.Residential)))
                .AccountNumber);
        }

        Assert.Equal(["C-000001", "C-000002", "C-000003"], issued);
    }

    private async Task<(int Customers, int AuditEntries)> CountAsync()
    {
        await using var scope = fixture.CreateScope();

        return (
            await scope.ServiceProvider.GetRequiredService<CustomersDbContext>().Customers.CountAsync(),
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().AuditEntries.CountAsync());
    }
}
