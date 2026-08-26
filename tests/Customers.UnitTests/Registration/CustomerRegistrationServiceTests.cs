using GridCore.Contracts.Events;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Registration;

/// <summary>
/// The intake wizard's one commit, over the real EF model on SQLite in-memory.
/// </summary>
/// <remarks>
/// The thing worth proving here is atomicity: an intake writes a customer, a premise, an account,
/// four audit entries and four events, and either all of it lands or none of it does. A wizard
/// abandoned or refused mid-flow must leave nothing behind, which is the fast tier's job because
/// the nested units of work are what make it true.
/// </remarks>
public class CustomerRegistrationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A CSR: may write to the registry and may take a deposit.</summary>
    private static CustomersTestHost NewHost() =>
        new(new FakeClock(Now), FakeCurrentUser.Holding(Permissions.Customers.Write, Permissions.Customers.Deposit));

    /// <summary>A clerk who may register a customer but may not take money off them.</summary>
    private static CustomersTestHost NewHostWithoutDepositPermission() =>
        new(new FakeClock(Now), FakeCurrentUser.Holding(Permissions.Customers.Write));

    private static ServiceLocationInput APremise() =>
        new(Address.Create("77 As Nieves Road", "Songsong", "Rota", "MP"), "Meter on the north wall");

    private static CustomerIntakeInput AnIntake(
        decimal deposit = 0m,
        bool startService = false,
        IntakePremise? premise = null) =>
        new(
            "Reyes Family Residence",
            CustomerClass.Residential,
            premise ?? new IntakePremise(APremise()),
            "Ana Reyes",
            "ana.reyes@example.com",
            "+1-670-532-0199",
            deposit,
            startService,
            "New connection");

    [Fact]
    public async Task An_intake_registers_the_customer_the_premise_and_the_account()
    {
        using var host = NewHost();

        var registration = await host.WithIntakeAsync(intake => intake.RegisterAsync(AnIntake()));

        Assert.Equal("C-000001", registration.Customer.AccountNumber);
        Assert.Equal("L-000001", registration.Location.LocationCode);
        Assert.Equal("A-000001", registration.Account.AccountNumber);
        Assert.True(registration.LocationWasRegistered);

        // Opened but not energised: starting service is a separate act, and this intake did not ask.
        Assert.Equal(ServiceAccountStatus.Pending, registration.Account.Status);
        Assert.Equal(registration.Customer.Id, registration.Account.CustomerId);
        Assert.Equal(registration.Location.Id, registration.Account.ServiceLocationId);

        await using var database = host.NewCustomersContext();

        Assert.Single(await database.Customers.ToListAsync());
        Assert.Single(await database.ServiceLocations.ToListAsync());
        Assert.Single(await database.ServiceAccounts.ToListAsync());
    }

    [Fact]
    public async Task An_intake_may_energise_supply_as_part_of_the_same_commit()
    {
        using var host = NewHost();

        var registration = await host.WithIntakeAsync(intake => intake.RegisterAsync(AnIntake(startService: true)));

        Assert.Equal(ServiceAccountStatus.Active, registration.Account.Status);
        Assert.Equal(Now, registration.Account.ServiceStartedAt);

        // Two history lines, opened and started — the account registry's own record, unchanged by
        // being driven from a wizard rather than from the account screen. Asserted as a set rather
        // than a sequence: both lines are stamped from the same frozen test clock, so their Guid v7
        // ids share a timestamp and the order between them is the random tail's, not the walk's.
        await using var database = host.NewCustomersContext();

        var history = await database.ServiceAccountHistory.ToListAsync();

        Assert.Equal(
            [ServiceAccountStatus.Pending, ServiceAccountStatus.Active],
            history.Select(entry => entry.ToStatus).OrderBy(status => status));

        host.Events.Single<ServiceStarted>();
    }

    [Fact]
    public async Task An_intake_may_open_the_account_at_a_premise_already_on_the_books()
    {
        using var host = NewHost();

        var existing = await host.WithLocationsAsync(locations => locations.RegisterAsync(APremise()));

        var registration = await host.WithIntakeAsync(intake =>
            intake.RegisterAsync(AnIntake(premise: new IntakePremise(ServiceLocationId: existing.Id))));

        Assert.Equal(existing.Id, registration.Location.Id);
        Assert.False(registration.LocationWasRegistered);

        await using var database = host.NewCustomersContext();

        // The premise was reused, not registered a second time under a second code.
        Assert.Single(await database.ServiceLocations.ToListAsync());
    }

    [Fact]
    public async Task An_intake_publishes_the_facts_each_registry_publishes_on_its_own()
    {
        using var host = NewHost();

        await host.WithIntakeAsync(intake => intake.RegisterAsync(AnIntake(startService: true)));

        // Composed, not reimplemented: the events are the registries' own, in the order the intake
        // performs them.
        Assert.Equal(
            [
                nameof(ServiceLocationRegistered),
                nameof(CustomerRegistered),
                nameof(ServiceAccountOpened),
                nameof(ServiceStarted),
            ],
            host.Events.Published.Select(published => published.GetType().Name));
    }

    [Fact]
    public async Task Collecting_a_deposit_records_it_on_the_customer_and_audits_what_was_asked_for()
    {
        using var host = NewHost();

        var assessed = DepositRules.All.Single(rule => rule.CustomerClass == CustomerClass.Residential).Amount;

        var registration = await host.WithIntakeAsync(intake => intake.RegisterAsync(AnIntake(deposit: assessed)));

        Assert.Equal(assessed, registration.Customer.DepositHeld);
        Assert.Equal(assessed, registration.Assessment.Amount);
        Assert.Equal(assessed, registration.DepositCollected);

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries
            .Where(candidate => candidate.Action == AuditActions.CustomerDepositCollected)
            .SingleAsync();

        Assert.Equal(AuditEntityTypes.Customer, entry.EntityType);
        Assert.Equal(registration.Customer.Id.ToString(), entry.EntityId);
        Assert.Equal("auth0|cs-agent", entry.UserId);

        // What was asked for beside what was taken, and the rule that said so — the only place the
        // difference between the two is recorded.
        Assert.Contains(registration.Assessment.RuleId.ToString(), entry.AfterJson, StringComparison.Ordinal);
        Assert.Contains("75.00", entry.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_intake_that_collects_a_deposit_hands_off_to_the_lifecycle_rather_than_duplicating_it()
    {
        // WORK_PACKAGES.md asked WP-2.8 to "hand off to the WP-2.12 lifecycle rather than
        // duplicating it", and WP-2.12 is what made that possible. The proof is that the intake
        // leaves a real LEDGER ENTRY and a real EVENT behind, not a figure written onto the customer
        // row: without them the balance would be a number Finance never heard about.
        using var host = NewHost();

        var registration = await host.WithIntakeAsync(intake => intake.RegisterAsync(AnIntake(deposit: 75.00m)));

        await using var database = host.NewCustomersContext();

        var entry = await database.DepositEntries.SingleAsync();

        Assert.Equal(registration.Customer.Id, entry.CustomerId);
        Assert.Equal(DepositEntryKind.Collected, entry.Kind);
        Assert.Equal(75.00m, entry.Amount);
        Assert.Equal(75.00m, entry.BalanceAfter);

        var published = host.Events.Single<CustomerDepositCollected>();

        Assert.Equal(entry.Id, published.DepositEntryId);
        Assert.Equal(registration.Customer.AccountNumber, published.AccountNumber);
    }

    [Fact]
    public async Task An_intake_refused_after_the_deposit_leaves_no_ledger_entry_behind()
    {
        // The collection runs inside the intake's transaction against a customer that has been
        // added but not yet saved — the deposit service finds it through the change tracker. This is
        // the other half of that: an intake that fails later takes the deposit entry with it, so an
        // abandoned wizard cannot leave money recorded against a customer who does not exist.
        using var host = NewHost();

        var premise = await host.WithLocationsAsync(locations => locations.RegisterAsync(APremise()));

        var sittingTenant = await host.WithCustomersAsync(customers =>
            customers.RegisterAsync(new RegisterCustomerInput("Sitting tenant", CustomerClass.Residential)));

        await host.WithAccountsAsync(accounts =>
            accounts.OpenAsync(new OpenServiceAccountInput(sittingTenant.Id, premise.Id)));

        // The premise is already served, so the ACCOUNT step throws — after the deposit was taken.
        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithIntakeAsync(intake => intake.RegisterAsync(
                AnIntake(premise: new IntakePremise(ServiceLocationId: premise.Id), deposit: 75.00m))));

        await using var database = host.NewCustomersContext();

        Assert.Empty(await database.DepositEntries.ToListAsync());
    }

    [Fact]
    public async Task A_waived_deposit_needs_no_permission_and_writes_no_deposit_entry()
    {
        // Nothing changed hands, so there is nothing to gate and nothing to audit as a collection.
        using var host = NewHostWithoutDepositPermission();

        var registration = await host.WithIntakeAsync(intake => intake.RegisterAsync(AnIntake(deposit: 0m)));

        Assert.Equal(0m, registration.Customer.DepositHeld);

        await using var platform = host.NewPlatformContext();

        Assert.Empty(await platform.AuditEntries
            .Where(entry => entry.Action == AuditActions.CustomerDepositCollected)
            .ToListAsync());
    }

    [Fact]
    public async Task Part_of_the_assessed_deposit_may_be_collected()
    {
        // A part-payment at the counter is ordinary; the balance is a receivable WP-2.12 tracks.
        using var host = NewHost();

        var registration = await host.WithIntakeAsync(intake => intake.RegisterAsync(AnIntake(deposit: 25.00m)));

        Assert.Equal(25.00m, registration.Customer.DepositHeld);
        Assert.Equal(75.00m, registration.Assessment.Amount);
    }

    [Fact]
    public async Task Collecting_a_deposit_without_the_permission_is_refused_and_writes_nothing()
    {
        // THE FAILURE PATH the work package names: 403, and an intake that leaves no trace.
        using var host = NewHostWithoutDepositPermission();

        var exception = await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.WithIntakeAsync(intake => intake.RegisterAsync(AnIntake(deposit: 75.00m))));

        Assert.Contains(Permissions.Customers.Deposit, exception.Message, StringComparison.Ordinal);

        await using var database = host.NewCustomersContext();
        await using var platform = host.NewPlatformContext();

        Assert.Empty(await database.Customers.ToListAsync());
        Assert.Empty(await database.ServiceLocations.ToListAsync());
        Assert.Empty(await database.ServiceAccounts.ToListAsync());
        Assert.Empty(await platform.AuditEntries.ToListAsync());
        Assert.Empty(host.Events.Published);
    }

    [Fact]
    public async Task Collecting_more_than_the_schedule_asks_for_is_refused()
    {
        using var host = NewHost();

        var exception = await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithIntakeAsync(intake => intake.RegisterAsync(AnIntake(deposit: 500.00m))));

        // The message quotes both figures: a clerk who typed a hundred too many needs to see which
        // hundred was expected, not just that something was wrong.
        Assert.Contains("75.00", exception.Message, StringComparison.Ordinal);
        Assert.Contains("500.00", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deposit_finer_than_a_cent_is_refused_rather_than_rounded()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithIntakeAsync(intake => intake.RegisterAsync(AnIntake(deposit: 25.125m))));
    }

    [Fact]
    public async Task An_intake_naming_no_premise_is_refused_before_anything_is_written()
    {
        using var host = NewHost();

        var exception = await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithIntakeAsync(intake => intake.RegisterAsync(AnIntake(premise: new IntakePremise()))));

        Assert.Contains("premise", exception.Message, StringComparison.Ordinal);

        await using var database = host.NewCustomersContext();

        Assert.Empty(await database.Customers.ToListAsync());
    }

    [Fact]
    public async Task An_intake_naming_two_premises_is_refused()
    {
        using var host = NewHost();

        var existing = await host.WithLocationsAsync(locations => locations.RegisterAsync(APremise()));

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithIntakeAsync(intake =>
                intake.RegisterAsync(AnIntake(premise: new IntakePremise(APremise(), existing.Id)))));
    }

    [Fact]
    public async Task An_intake_at_a_premise_that_is_already_served_leaves_nothing_behind()
    {
        // The wizard's last step is the one that fails, which is exactly the case the single
        // transaction exists for: the customer and the premise were already "written" when the
        // account registry refused, and neither survives.
        using var host = NewHost();

        var first = await host.WithIntakeAsync(intake => intake.RegisterAsync(AnIntake()));

        var exception = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithIntakeAsync(intake => intake.RegisterAsync(
                AnIntake(premise: new IntakePremise(ServiceLocationId: first.Location.Id)))));

        Assert.Contains(first.Account.AccountNumber, exception.Message, StringComparison.Ordinal);

        await using var database = host.NewCustomersContext();

        // Only the first intake's rows: the second wrote no second customer.
        Assert.Single(await database.Customers.ToListAsync());
        Assert.Single(await database.ServiceAccounts.ToListAsync());
    }

    [Fact]
    public async Task An_intake_at_a_premise_that_does_not_exist_is_a_not_found()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<ServiceLocationNotFoundException>(() =>
            host.WithIntakeAsync(intake =>
                intake.RegisterAsync(AnIntake(premise: new IntakePremise(ServiceLocationId: Guid.CreateVersion7())))));
    }

    [Fact]
    public async Task An_intake_at_a_deactivated_premise_is_refused_and_writes_no_customer()
    {
        using var host = NewHost();

        var closed = await host.WithLocationsAsync(locations =>
            locations.RegisterAsync(new ServiceLocationInput(APremise().Address, "Condemned", IsActive: false)));

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithIntakeAsync(intake =>
                intake.RegisterAsync(AnIntake(premise: new IntakePremise(ServiceLocationId: closed.Id)))));

        await using var database = host.NewCustomersContext();

        Assert.Empty(await database.Customers.ToListAsync());
    }

    [Fact]
    public async Task Every_write_an_intake_performs_is_audited()
    {
        using var host = NewHost();

        await host.WithIntakeAsync(intake => intake.RegisterAsync(AnIntake(deposit: 75.00m, startService: true)));

        await using var platform = host.NewPlatformContext();

        var actions = await platform.AuditEntries.Select(entry => entry.Action).ToListAsync();

        // Invariant 1 for each composed write, plus invariant 5's entry for the sensitive one.
        // A set, not a sequence: every entry is stamped from the same frozen clock, so the order
        // between them is their ids' random tails rather than the order the intake performed them.
        Assert.Equal(
            new[]
            {
                AuditActions.CustomerCreated,
                AuditActions.CustomerDepositCollected,
                AuditActions.ServiceAccountOpened,
                AuditActions.ServiceAccountStarted,
                AuditActions.ServiceLocationCreated,
            }.Order(StringComparer.Ordinal),
            actions.Order(StringComparer.Ordinal));
    }
}
