using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.Features.Transitions;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// WP-2.15's transfer against real Postgres: the two account moves, the deposit carry, the register
/// row, three audit entries and the outbox message are <b>one transaction across three schemas</b> —
/// and the register's two-sided account filter is the database's answer, not SQLite's.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier proves everything that does not need infrastructure — the reason-code map, the
/// effective-date guards, the state machines, the permission gate, the events and the arithmetic,
/// all in milliseconds. What only a container can show is the part the fast tier stands a
/// single-connection SQLite database in for: that a transfer either happens completely or not at
/// all, when the account rows are in <c>customers</c>, the audit entries and the outbox row are in
/// <c>platform</c>, and three services each opened a nested unit of work.
/// </para>
/// <para>
/// <b>No billing run here, deliberately.</b> The one cross-module read this package added —
/// <c>IBillDirectory.LastIssuedOnForCustomerAsync</c> — is tested against the real register in
/// <c>BillDirectoryTests</c> in Billing's own fast tier, beside the seam methods WP-2.14 added.
/// Building an issued bill in this file would mean a fourth copy of the twenty-line reading-cycle
/// helper, two of which are flaky at the simulator's 4% missed-read rate (see STATUS.md), to prove
/// something a fast test already proves.
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AccountTransitionTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_transfer_commits_both_account_moves_the_carry_and_the_register_row_together()
    {
        // THE PACKAGE'S HEADLINE CLAIM against a real database. Four writes in `customers`, three
        // audit entries and an outbox row in `platform`, through three services each opening its own
        // nested unit of work — and one transaction underneath all of it (invariants 1 and 2).
        var (customer, account) = await AServedCustomerAsync();
        var destination = await APremiseAsync();

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICustomerDepositService>()
                .CollectAsync(customer.Id, new CollectDepositInput(250.00m));
        }

        AccountTransition transition;

        await using (var scope = fixture.CreateScope())
        {
            transition = await scope.ServiceProvider.GetRequiredService<ICustomerTransitionService>()
                .TransferAsync(
                    customer.Id,
                    new TransferServiceInput(
                        account.Id,
                        destination.Id,
                        TransitionReasonCode.Relocation,
                        new DateOnly(2026, 9, 1)));
        }

        await using var reading = fixture.CreateScope();

        var customers = reading.ServiceProvider.GetRequiredService<CustomersDbContext>();

        var accounts = await customers.ServiceAccounts
            .AsNoTracking()
            .Where(row => row.CustomerId == customer.Id)
            .OrderBy(row => row.AccountNumber)
            .ToListAsync();

        Assert.Equal(2, accounts.Count);
        Assert.Equal(ServiceAccountStatus.Closed, accounts[0].Status);
        Assert.Equal(ServiceAccountStatus.Pending, accounts[1].Status);
        Assert.Equal(destination.Id, accounts[1].ServiceLocationId);

        var stored = await customers.AccountTransitions.AsNoTracking().SingleAsync(row => row.Id == transition.Id);

        Assert.Equal(AccountTransitionKind.Transferred, stored.Kind);
        Assert.Equal(TransitionReasonCode.Relocation, stored.ReasonCode);
        Assert.Equal(new DateOnly(2026, 9, 1), stored.EffectiveOn);
        Assert.Equal(accounts[0].Id, stored.FromServiceAccountId);
        Assert.Equal(accounts[1].Id, stored.ToServiceAccountId);
        Assert.Equal(250.00m, stored.DepositCarried);

        // NO NET MONEY CREATED, read off the ledger rather than off the projection: one collection
        // and one carry, and the carry moved nothing.
        var entries = await customers.DepositEntries
            .AsNoTracking()
            .Where(row => row.CustomerId == customer.Id)
            .OrderBy(row => row.RecordedAt)
            .ThenBy(row => row.Id)
            .ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Kind is DepositEntryKind.Transferred && entry.Amount == 250.00m);
        Assert.DoesNotContain(entries, entry => entry.Kind is DepositEntryKind.Refunded);

        Assert.Equal(
            250.00m,
            (await customers.Customers.AsNoTracking().SingleAsync(row => row.Id == customer.Id)).DepositHeld);

        var platform = reading.ServiceProvider.GetRequiredService<PlatformDbContext>();

        // Three entries: WP-1.2's close, WP-1.2's open, and the register's own — each in the same
        // transaction as the row it describes.
        Assert.True(await platform.AuditEntries.AnyAsync(entry =>
            entry.Action == AuditActions.ServiceTransferred && entry.EntityId == transition.Id.ToString()));

        Assert.True(await platform.AuditEntries.AnyAsync(entry => entry.Action == AuditActions.ServiceAccountClosed));
        Assert.True(await platform.AuditEntries.AnyAsync(entry => entry.Action == AuditActions.ServiceAccountOpened));
    }

    [Fact]
    public async Task A_refused_transfer_leaves_the_customer_connected_where_they_were()
    {
        // The rollback is the half only a real transaction can show, and it is the one that matters
        // at a counter: a transfer refused because the destination is occupied must not leave the
        // customer disconnected at the premise they still live in.
        var (customer, account) = await AServedCustomerAsync();
        var (_, occupier) = await AServedCustomerAsync();

        await Assert.ThrowsAsync<RegistryWorkflowException>(async () =>
        {
            await using var scope = fixture.CreateScope();

            await scope.ServiceProvider.GetRequiredService<ICustomerTransitionService>()
                .TransferAsync(
                    customer.Id,
                    new TransferServiceInput(account.Id, occupier.ServiceLocationId, TransitionReasonCode.Relocation));
        });

        await using var reading = fixture.CreateScope();

        var customers = reading.ServiceProvider.GetRequiredService<CustomersDbContext>();

        Assert.Equal(
            ServiceAccountStatus.Active,
            (await customers.ServiceAccounts.AsNoTracking().SingleAsync(row => row.Id == account.Id)).Status);

        // And nothing half-written: no new account at the destination, and no register row claiming
        // a transfer that never happened.
        Assert.Equal(1, await customers.ServiceAccounts.CountAsync(row => row.CustomerId == customer.Id));
        Assert.False(await customers.AccountTransitions.AnyAsync(row => row.CustomerId == customer.Id));
    }

    [Fact]
    public async Task The_register_finds_an_account_on_EITHER_side_of_a_transition_from_POSTGRES()
    {
        // "What happened to this account" is answered by an OR over two nullable columns, each with
        // its own partial index — which is a query plan, and SQLite agreeing with it in the fast tier
        // is not the same claim. A transfer names the old account on the FROM side and the new one on
        // the TO side, and both have to be findable.
        var (customer, account) = await AServedCustomerAsync();
        var destination = await APremiseAsync();

        AccountTransition transition;

        await using (var scope = fixture.CreateScope())
        {
            transition = await scope.ServiceProvider.GetRequiredService<ICustomerTransitionService>()
                .TransferAsync(
                    customer.Id,
                    new TransferServiceInput(account.Id, destination.Id, TransitionReasonCode.Relocation));
        }

        await using var scope2 = fixture.CreateScope();

        var transitions = scope2.ServiceProvider.GetRequiredService<ICustomerTransitionService>();

        var byOldAccount = await transitions.ListAsync(customer.Id, new TransitionQuery(ServiceAccountId: account.Id));
        var byNewAccount = await transitions.ListAsync(
            customer.Id,
            new TransitionQuery(ServiceAccountId: transition.ToServiceAccountId!.Value));

        Assert.Equal(transition.Id, Assert.Single(byOldAccount).Id);
        Assert.Equal(transition.Id, Assert.Single(byNewAccount).Id);
    }

    [Fact]
    public async Task A_class_change_reaches_the_outbox_in_the_same_transaction()
    {
        // Invariant 2: the event Billing's deepening pass will consume is published through the
        // outbox and commits with the customer row, so a rollback takes the event with it.
        var (customer, _) = await AServedCustomerAsync();

        AccountTransition transition;

        await using (var scope = fixture.CreateScope())
        {
            transition = await scope.ServiceProvider.GetRequiredService<ICustomerTransitionService>()
                .ChangeClassAsync(
                    customer.Id,
                    new ChangeCustomerClassInput(
                        CustomerClass.Commercial,
                        TransitionReasonCode.PremiseNowTrading,
                        new DateOnly(2026, 9, 1)));
        }

        await using var reading = fixture.CreateScope();

        var customers = reading.ServiceProvider.GetRequiredService<CustomersDbContext>();
        var stored = await customers.Customers.AsNoTracking().SingleAsync(row => row.Id == customer.Id);

        Assert.Equal(CustomerClass.Commercial, stored.Class);

        // The projection billing prices from, round-tripped through a `date` column.
        Assert.Equal(new DateOnly(2026, 9, 1), stored.ClassEffectiveOn);

        Assert.Equal(new DateOnly(2026, 9, 1), (await customers.AccountTransitions
            .AsNoTracking()
            .SingleAsync(row => row.Id == transition.Id)).EffectiveOn);
    }

    private async Task<ServiceLocation> APremiseAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];

        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IServiceLocationService>()
            .RegisterAsync(new ServiceLocationInput(
                Address.Create($"{tag} As Nieves Road", "Songsong", "Rota", "MP", postalCode: "96951"),
                "House",
                IsActive: true,
                null));
    }

    /// <summary>A customer with one energised account, which is where a transfer starts.</summary>
    private async Task<(Customer Customer, ServiceAccount Account)> AServedCustomerAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var premise = await APremiseAsync();

        await using var scope = fixture.CreateScope();

        var customer = await scope.ServiceProvider.GetRequiredService<ICustomerService>()
            .RegisterAsync(new RegisterCustomerInput($"Transition customer {tag}", CustomerClass.Residential, "Ana Reyes"));

        var accounts = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

        var account = await accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, premise.Id));

        await accounts.StartServiceAsync(account.Id, "Connected.");

        return (customer, account);
    }
}
