using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Profile;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Profile;

/// <summary>
/// The customer profile over the real EF model, on SQLite in-memory. The fallback is the thing
/// under test: post goes to the service address until somebody says otherwise, and which service
/// address that is is resolved on every read rather than copied into the row.
/// </summary>
public class CustomerProfileServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private static CustomersTestHost NewHost() =>
        new(new FakeClock(Now), new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    private static Task<Customer> ACustomerAsync(CustomersTestHost host, string? email = "maria.sablan@example.com") =>
        host.WithCustomersAsync(customers => customers.RegisterAsync(
            new RegisterCustomerInput("Sablan Family Residence", CustomerClass.Residential, "Maria Sablan", email, "+1-670-532-0114")));

    private static async Task<ServiceAccount> AnAccountAsync(CustomersTestHost host, Customer customer, string line1)
    {
        var location = await host.WithLocationsAsync(locations => locations.RegisterAsync(
            new ServiceLocationInput(Address.Create(line1, "Songsong", "Rota", "MP"), "House")));

        return await host.WithAccountsAsync(accounts => accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, location.Id, ServiceType.Electricity)));
    }

    private static Address AMailingAddress() =>
        Address.Create("PO Box 501", "Songsong", "Rota", "MP", postalCode: "96951");

    private static UpdateCustomerProfileInput Preferences(Address? mailingAddress = null, BillDeliveryChannel channel = BillDeliveryChannel.Post) =>
        new(mailingAddress, channel, OutageNotices: true, DunningNotices: true, CommunicationLanguage.English);

    [Fact]
    public async Task A_customer_nobody_has_saved_a_profile_for_reads_back_as_the_defaults()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        var view = await host.WithProfileAsync(profiles => profiles.GetAsync(customer.Id));

        // A null UpdatedAt is the difference between "nobody has said" and "somebody chose exactly
        // this", and the row is only written the first time a rep saves.
        Assert.Null(view.UpdatedAt);
        Assert.Equal(BillDeliveryChannel.Post, view.BillDeliveryChannel);
        Assert.True(view.OutageNotices);
        Assert.True(view.DunningNotices);

        await using var database = host.NewCustomersContext();

        Assert.Empty(await database.CustomerProfiles.ToListAsync());
    }

    [Fact]
    public async Task A_customer_with_no_accounts_and_no_override_has_nowhere_to_post_to()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        var view = await host.WithProfileAsync(profiles => profiles.GetAsync(customer.Id));

        Assert.Equal(MailingAddressSource.None, view.Source);
        Assert.Null(view.MailingAddress);
        Assert.Null(view.ServiceAddress);
    }

    [Fact]
    public async Task The_mailing_address_defaults_to_the_service_address()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        await AnAccountAsync(host, customer, "12 Sinapalo Drive");

        var view = await host.WithProfileAsync(profiles => profiles.GetAsync(customer.Id));

        Assert.Equal(MailingAddressSource.ServiceAddress, view.Source);
        Assert.Equal("12 Sinapalo Drive", view.MailingAddress!.Line1);
        Assert.Equal("12 Sinapalo Drive", view.ServiceAddress!.Line1);
    }

    [Fact]
    public async Task The_default_follows_the_most_recently_active_account()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        var first = await AnAccountAsync(host, customer, "12 Sinapalo Drive");
        await host.WithAccountsAsync(accounts => accounts.StartServiceAsync(first.Id, null));
        await host.WithAccountsAsync(accounts => accounts.CloseAsync(first.Id, "Moved out"));

        await AnAccountAsync(host, customer, "3 Tatachog Road");

        // Resolved on every read rather than stored: a customer who transfers premises would
        // otherwise keep getting post at the house they left, with nothing on the screen to say why.
        var view = await host.WithProfileAsync(profiles => profiles.GetAsync(customer.Id));

        Assert.Equal("3 Tatachog Road", view.MailingAddress!.Line1);
    }

    [Fact]
    public async Task An_override_takes_the_place_of_the_service_address()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        await AnAccountAsync(host, customer, "12 Sinapalo Drive");

        var view = await host.WithProfileAsync(profiles => profiles.UpdateAsync(customer.Id, Preferences(AMailingAddress())));

        Assert.Equal(MailingAddressSource.Override, view.Source);
        Assert.Equal("PO Box 501", view.MailingAddress!.Line1);

        // The default is still reported beside it, so a screen can show what clearing it would do.
        Assert.Equal("12 Sinapalo Drive", view.ServiceAddress!.Line1);
    }

    [Fact]
    public async Task Clearing_the_override_falls_back_to_the_service_address()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        await AnAccountAsync(host, customer, "12 Sinapalo Drive");

        await host.WithProfileAsync(profiles => profiles.UpdateAsync(customer.Id, Preferences(AMailingAddress())));
        var cleared = await host.WithProfileAsync(profiles => profiles.UpdateAsync(customer.Id, Preferences(null)));

        Assert.Equal(MailingAddressSource.ServiceAddress, cleared.Source);
        Assert.Equal("12 Sinapalo Drive", cleared.MailingAddress!.Line1);
    }

    [Fact]
    public async Task Saving_a_profile_twice_writes_one_row()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        await host.WithProfileAsync(profiles => profiles.UpdateAsync(customer.Id, Preferences(AMailingAddress())));
        await host.WithProfileAsync(profiles => profiles.UpdateAsync(customer.Id, Preferences(null)));

        await using var database = host.NewCustomersContext();

        // The customer IS the key, so one profile per customer is a database fact rather than
        // something the service remembers to check.
        var stored = Assert.Single(await database.CustomerProfiles.ToListAsync());

        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Null(stored.MailingAddress);
    }

    [Fact]
    public async Task Email_delivery_is_refused_when_the_customer_has_no_email()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host, email: null);

        var refusal = await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithProfileAsync(profiles => profiles.UpdateAsync(customer.Id, Preferences(channel: BillDeliveryChannel.Email))));

        Assert.Contains(customer.AccountNumber, refusal.Message, StringComparison.Ordinal);

        await using var database = host.NewCustomersContext();

        Assert.Empty(await database.CustomerProfiles.ToListAsync());
    }

    [Fact]
    public async Task Both_needs_an_email_too_because_half_of_it_is_email()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host, email: null);

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithProfileAsync(profiles => profiles.UpdateAsync(customer.Id, Preferences(channel: BillDeliveryChannel.Both))));
    }

    [Fact]
    public async Task Post_is_always_allowed()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host, email: null);

        var view = await host.WithProfileAsync(profiles => profiles.UpdateAsync(customer.Id, Preferences(channel: BillDeliveryChannel.Post)));

        Assert.Equal(BillDeliveryChannel.Post, view.BillDeliveryChannel);
    }

    [Fact]
    public async Task The_first_save_audits_a_null_before()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        await host.WithProfileAsync(profiles => profiles.UpdateAsync(customer.Id, Preferences(AMailingAddress())));

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.CustomerProfileUpdated);

        Assert.Equal(AuditEntityTypes.CustomerProfile, entry.EntityType);
        Assert.Equal(customer.Id.ToString(), entry.EntityId);
        Assert.Equal("auth0|cs-agent", entry.UserId);

        // Null rather than the defaults: there was no stored profile, and recording the defaults as
        // though somebody had chosen them would make the trail claim a decision nobody made.
        Assert.Null(entry.BeforeJson);
        Assert.Contains("PO Box 501", entry.AfterJson);
    }

    [Fact]
    public async Task A_later_save_audits_the_before_and_the_after()
    {
        // The clock is advanced between the two saves rather than left still: audit ids are Guid v7
        // stamped from the instant, so two entries written in the same millisecond have no order
        // this test could rely on — the trap WP-2.10's timeline documents for same-instant clusters.
        var clock = new FakeClock(Now);

        using var host = new CustomersTestHost(clock, new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));
        var customer = await ACustomerAsync(host);

        await host.WithProfileAsync(profiles => profiles.UpdateAsync(customer.Id, Preferences(AMailingAddress())));

        clock.Advance(TimeSpan.FromMinutes(5));

        await host.WithProfileAsync(profiles => profiles.UpdateAsync(
            customer.Id,
            new UpdateCustomerProfileInput(null, BillDeliveryChannel.Email, false, true, CommunicationLanguage.Chamorro)));

        await using var platform = host.NewPlatformContext();

        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset, and there are two rows.
        var entry = (await platform.AuditEntries
                .Where(candidate => candidate.Action == AuditActions.CustomerProfileUpdated)
                .ToListAsync())
            .MaxBy(candidate => candidate.OccurredAt)!;

        Assert.Contains("PO Box 501", entry.BeforeJson);
        Assert.Contains(nameof(CommunicationLanguage.Chamorro), entry.AfterJson);
        Assert.DoesNotContain("PO Box 501", entry.AfterJson);
    }

    [Fact]
    public async Task A_refused_save_leaves_no_profile_and_no_audit_entry()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host, email: null);

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithProfileAsync(profiles => profiles.UpdateAsync(customer.Id, Preferences(channel: BillDeliveryChannel.Email))));

        await using var platform = host.NewPlatformContext();

        Assert.False(await platform.AuditEntries.AnyAsync(entry => entry.EntityType == AuditEntityTypes.CustomerProfile));
    }

    [Fact]
    public async Task A_customer_who_does_not_exist_is_not_found()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<CustomerNotFoundException>(() =>
            host.WithProfileAsync(profiles => profiles.GetAsync(Guid.CreateVersion7(Now))));

        await Assert.ThrowsAsync<CustomerNotFoundException>(() =>
            host.WithProfileAsync(profiles => profiles.UpdateAsync(Guid.CreateVersion7(Now), Preferences())));
    }
}
