using GridCore.Contracts.Events;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>
/// The customer registry over the real EF model, on SQLite in-memory. What these prove that the
/// aggregate tests cannot: the write, its audit entry and its event are one transaction, and the
/// account number series continues correctly across registrations.
/// </summary>
public class CustomerServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static CustomersTestHost NewHost() =>
        new(new FakeClock(Now), new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    private static RegisterCustomerInput AResidentialCustomer(string name = "Sablan Family Residence") =>
        new(name, CustomerClass.Residential, "Maria Sablan", "maria.sablan@example.com", "+1-670-532-0114", 75.00m);

    [Fact]
    public async Task Registering_a_customer_issues_the_first_account_number()
    {
        using var host = NewHost();

        var customer = await host.WithCustomersAsync(customers => customers.RegisterAsync(AResidentialCustomer()));

        Assert.Equal("C-000001", customer.AccountNumber);
        Assert.Equal(CustomerStatus.Prospect, customer.Status);

        await using var database = host.NewCustomersContext();

        Assert.Equal("C-000001", (await database.Customers.SingleAsync()).AccountNumber);
    }

    [Fact]
    public async Task Each_registration_continues_the_series()
    {
        using var host = NewHost();

        await host.WithCustomersAsync(customers => customers.RegisterAsync(AResidentialCustomer("First")));
        await host.WithCustomersAsync(customers => customers.RegisterAsync(AResidentialCustomer("Second")));
        var third = await host.WithCustomersAsync(customers => customers.RegisterAsync(AResidentialCustomer("Third")));

        Assert.Equal("C-000003", third.AccountNumber);
    }

    [Fact]
    public async Task Registering_a_customer_audits_it_and_publishes_the_fact()
    {
        using var host = NewHost();

        var customer = await host.WithCustomersAsync(customers => customers.RegisterAsync(AResidentialCustomer()));

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries.SingleAsync();

        Assert.Equal(AuditActions.CustomerCreated, entry.Action);
        Assert.Equal(AuditEntityTypes.Customer, entry.EntityType);
        Assert.Equal(customer.Id.ToString(), entry.EntityId);
        Assert.Equal("auth0|cs-agent", entry.UserId);
        Assert.Null(entry.BeforeJson);
        Assert.Contains("C-000001", entry.AfterJson);

        var published = host.Events.Single<CustomerRegistered>();

        Assert.Equal(customer.Id, published.CustomerId);
        Assert.Equal("C-000001", published.AccountNumber);
        Assert.Equal(nameof(CustomerClass.Residential), published.CustomerClass);
        Assert.Equal(Now, published.OccurredAt);
    }

    [Fact]
    public async Task A_registration_that_fails_leaves_no_customer_and_no_audit_entry()
    {
        // The whole point of the shared unit of work: the aggregate's guard throws inside the
        // transaction, so the row, its audit entry and its outbox row all roll back together.
        using var host = NewHost();

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithCustomersAsync(customers =>
                customers.RegisterAsync(new RegisterCustomerInput("Sablan Family Residence", CustomerClass.Residential, DepositHeld: -5m))));

        await using var database = host.NewCustomersContext();
        await using var platform = host.NewPlatformContext();

        Assert.Empty(await database.Customers.ToListAsync());
        Assert.Empty(await platform.AuditEntries.ToListAsync());
    }

    [Fact]
    public async Task Updating_a_customer_audits_what_it_looked_like_before()
    {
        using var host = NewHost();

        var customer = await host.WithCustomersAsync(customers => customers.RegisterAsync(AResidentialCustomer()));

        await host.WithCustomersAsync(customers => customers.UpdateAsync(
            customer.Id,
            new UpdateCustomerInput("Sablan Family Trust", CustomerClass.Commercial, DepositHeld: 150.00m)));

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries
            .Where(candidate => candidate.Action == AuditActions.CustomerUpdated)
            .SingleAsync();

        Assert.Contains("Sablan Family Residence", entry.BeforeJson);
        Assert.Contains("Sablan Family Trust", entry.AfterJson);

        // Stored by name, not by number: the entry has to still make sense if the enum is reordered.
        Assert.Contains(nameof(CustomerClass.Commercial), entry.AfterJson);
    }

    [Fact]
    public async Task Updating_a_customer_that_does_not_exist_is_a_404_not_a_silent_no_op()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<CustomerNotFoundException>(() =>
            host.WithCustomersAsync(customers => customers.UpdateAsync(
                Guid.CreateVersion7(Now),
                new UpdateCustomerInput("Nobody", CustomerClass.Residential))));
    }

    [Fact]
    public async Task Changing_status_is_audited_and_recorded_on_the_customer()
    {
        using var host = NewHost();

        var customer = await host.WithCustomersAsync(customers => customers.RegisterAsync(AResidentialCustomer()));

        var active = await host.WithCustomersAsync(customers =>
            customers.ChangeStatusAsync(customer.Id, CustomerStatus.Active, "Deposit received."));

        Assert.Equal(CustomerStatus.Active, active.Status);
        Assert.Equal(Now, active.StatusChangedAt);

        await using var platform = host.NewPlatformContext();

        Assert.Single(await platform.AuditEntries
            .Where(entry => entry.Action == AuditActions.CustomerStatusChanged)
            .ToListAsync());
    }

    [Fact]
    public async Task An_illegal_status_change_leaves_the_customer_alone()
    {
        // Failure path: the endpoint answers 409, and nothing about the customer moved — including
        // the "why" recorded against the last legal change.
        using var host = NewHost();

        var customer = await host.WithCustomersAsync(customers => customers.RegisterAsync(AResidentialCustomer()));

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithCustomersAsync(customers => customers.ChangeStatusAsync(customer.Id, CustomerStatus.Suspended, reason: null)));

        await using var database = host.NewCustomersContext();

        var stored = await database.Customers.SingleAsync();

        Assert.Equal(CustomerStatus.Prospect, stored.Status);
        Assert.Null(stored.StatusChangedAt);
    }

    [Fact]
    public async Task The_registry_list_filters_on_status_class_and_a_search_term()
    {
        using var host = NewHost();

        var residential = await host.WithCustomersAsync(customers => customers.RegisterAsync(AResidentialCustomer("Taisacan Household")));
        await host.WithCustomersAsync(customers => customers.RegisterAsync(
            new RegisterCustomerInput("Songsong Village Market", CustomerClass.Commercial)));

        await host.WithCustomersAsync(customers => customers.ChangeStatusAsync(residential.Id, CustomerStatus.Active, null));

        Assert.Equal(
            ["Taisacan Household"],
            (await host.WithCustomersAsync(customers => customers.ListAsync(new CustomerQuery(Status: CustomerStatus.Active))))
                .Select(customer => customer.Name));

        Assert.Equal(
            ["Songsong Village Market"],
            (await host.WithCustomersAsync(customers => customers.ListAsync(new CustomerQuery(Class: CustomerClass.Commercial))))
                .Select(customer => customer.Name));

        // Case-insensitive, and matching the account number as well as the name — a customer
        // service agent reads the number off a bill, in whatever case they happen to type it.
        Assert.Equal(
            ["Songsong Village Market"],
            (await host.WithCustomersAsync(customers => customers.ListAsync(new CustomerQuery(Search: "sONGSONG"))))
                .Select(customer => customer.Name));

        Assert.Single(await host.WithCustomersAsync(customers => customers.ListAsync(new CustomerQuery(Search: "c-000001"))));
    }

    [Fact]
    public async Task The_registry_list_is_newest_first_and_capped()
    {
        var clock = new FakeClock(Now);

        // Ids are Guid v7 stamped from the clock, and rows created in the same instant have no
        // defined order — so the clock has to move between registrations for "newest" to mean
        // anything at all.
        using var ordered = new CustomersTestHost(clock);

        foreach (var name in (string[])["First", "Second", "Third"])
        {
            await ordered.WithCustomersAsync(customers => customers.RegisterAsync(new RegisterCustomerInput(name, CustomerClass.Residential)));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(
            ["Third", "Second", "First"],
            (await ordered.WithCustomersAsync(customers => customers.ListAsync(new CustomerQuery())))
                .Select(customer => customer.Name));

        Assert.Single(await ordered.WithCustomersAsync(customers => customers.ListAsync(new CustomerQuery(Limit: 1))));
    }

    [Fact]
    public async Task A_customer_that_does_not_exist_is_found_as_null_rather_than_thrown()
    {
        using var host = NewHost();

        Assert.Null(await host.WithCustomersAsync(customers => customers.FindAsync(Guid.CreateVersion7(Now))));
    }
}
