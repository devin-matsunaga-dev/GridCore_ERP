using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GridCore.IntegrationTests;

/// <summary>
/// The service account registry against real Postgres. The fast tier proves the lifecycle, the
/// history and the cross-registry guards on SQLite; what a container adds is the one rule the
/// service can only ask the database to keep — the filtered unique index that stops two open
/// accounts sharing a premise even when the service's own check has already passed.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ServiceAccountRegistryTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_account_walks_its_lifecycle_and_keeps_the_history_in_the_customers_schema()
    {
        var (customerId, locationId) = await ARegisteredPairingAsync();

        Guid accountId;

        await using (var scope = fixture.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

            accountId = (await accounts.OpenAsync(new OpenServiceAccountInput(customerId, locationId, "Requested at the counter"))).Id;
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IServiceAccountService>()
                .StartServiceAsync(accountId, "Connection completed");
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IServiceAccountService>()
                .StopServiceAsync(accountId, "Disconnected for non-payment");
        }

        await using var read = fixture.CreateScope();

        var stored = await read.ServiceProvider.GetRequiredService<CustomersDbContext>()
            .ServiceAccounts.AsNoTracking()
            .Include(account => account.History)
            .SingleAsync(account => account.Id == accountId);

        Assert.Equal(ServiceAccountStatus.Disconnected, stored.Status);
        Assert.NotNull(stored.ServiceEndedAt);

        // Each transition committed on its own request, so the history is what Postgres holds
        // rather than what one change tracker remembers.
        Assert.Equal(
            [ServiceAccountStatus.Pending, ServiceAccountStatus.Active, ServiceAccountStatus.Disconnected],
            stored.History.OrderBy(entry => entry.Id).Select(entry => entry.ToStatus).ToArray());
    }

    [Fact]
    public async Task The_database_refuses_a_second_open_account_at_one_premise()
    {
        // The service checks first, so this inserts straight through the context to get past it —
        // which is exactly what a race between two requests does. The filtered unique index is the
        // only thing standing between that race and two accounts billing one meter.
        var (customerId, locationId) = await ARegisteredPairingAsync();
        var (otherCustomerId, _) = await ARegisteredPairingAsync("Taisacan Household", "14 Tatachog Street");

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IServiceAccountService>()
                .OpenAsync(new OpenServiceAccountInput(customerId, locationId));
        }

        await using var second = fixture.CreateScope();

        var database = second.ServiceProvider.GetRequiredService<CustomersDbContext>();

        database.ServiceAccounts.Add(ServiceAccount.Open(
            "A-999999",
            otherCustomerId,
            locationId,
            new RegistryActor("system", "system"),
            DateTimeOffset.UtcNow));

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());

        Assert.Equal("23505", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }

    [Fact]
    public async Task Closing_an_account_lets_the_premise_be_reissued()
    {
        var (customerId, locationId) = await ARegisteredPairingAsync();
        var (nextCustomerId, _) = await ARegisteredPairingAsync("Taisacan Household", "14 Tatachog Street");

        Guid accountId;

        await using (var scope = fixture.CreateScope())
        {
            accountId = (await scope.ServiceProvider.GetRequiredService<IServiceAccountService>()
                .OpenAsync(new OpenServiceAccountInput(customerId, locationId))).Id;
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IServiceAccountService>()
                .CloseAsync(accountId, "Tenant moved out");
        }

        await using var reissue = fixture.CreateScope();

        // A closed account releases its premise, so the partial index no longer covers the old row
        // and the next occupant can be connected.
        var reopened = await reissue.ServiceProvider.GetRequiredService<IServiceAccountService>()
            .OpenAsync(new OpenServiceAccountInput(nextCustomerId, locationId));

        Assert.Equal(ServiceAccountStatus.Pending, reopened.Status);
    }

    private async Task<(Guid CustomerId, Guid LocationId)> ARegisteredPairingAsync(
        string name = "Sablan Family Residence",
        string line1 = "128 As Nieves Road")
    {
        await using var scope = fixture.CreateScope();

        var customer = await scope.ServiceProvider.GetRequiredService<ICustomerService>()
            .RegisterAsync(new RegisterCustomerInput(name, CustomerClass.Residential));

        var location = await scope.ServiceProvider.GetRequiredService<IServiceLocationService>()
            .RegisterAsync(new ServiceLocationInput(
                Address.Create(line1, "Songsong", "Rota", "MP", postalCode: "96951"),
                "Single-storey house"));

        return (customer.Id, location.Id);
    }
}
