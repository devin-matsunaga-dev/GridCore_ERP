using GridCore.Modules.Customers.Features.Contacts;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Contacts;

/// <summary>
/// The contact register over the real EF model, on SQLite in-memory. What these prove that the
/// aggregate tests cannot: the write and its audit entry are one transaction, the disclosure
/// permission is enforced on exactly the acts that move the flag, and the method set survives a
/// round trip with its primary in the right place.
/// </summary>
public class CustomerContactServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A rep who may maintain contacts but may not decide who the utility talks to.</summary>
    private static CustomersTestHost HostWithoutAuthorise() =>
        new(new FakeClock(Now), FakeCurrentUser.Holding(Permissions.Customers.Read, Permissions.Customers.Write));

    private static CustomersTestHost NewHost() =>
        new(new FakeClock(Now), new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    private static Task<Customer> ACustomerAsync(CustomersTestHost host) =>
        host.WithCustomersAsync(customers => customers.RegisterAsync(
            new RegisterCustomerInput("Sablan Family Residence", CustomerClass.Residential, "Maria Sablan", "maria.sablan@example.com", "+1-670-532-0114")));

    [Fact]
    public async Task A_contact_is_added_with_its_methods_in_one_write()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        var contact = await host.WithContactsAsync(contacts => contacts.AddAsync(
            customer.Id,
            new AddCustomerContactInput(
                "Rosa Sablan",
                "Spouse",
                Methods:
                [
                    new AddContactMethodInput(ContactMethodKind.Mobile, "+1-670-285-1180"),
                    new AddContactMethodInput(ContactMethodKind.Email, "rosa@example.com"),
                ])));

        await using var database = host.NewCustomersContext();

        var stored = await database.CustomerContacts.Include(candidate => candidate.Methods).SingleAsync();

        Assert.Equal(contact.Id, stored.Id);
        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Equal(2, stored.Methods.Count);
        Assert.All(stored.Methods, method => Assert.True(method.IsPrimary));
    }

    [Fact]
    public async Task Adding_a_contact_audits_it_with_the_method_set()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        var contact = await host.WithContactsAsync(contacts => contacts.AddAsync(
            customer.Id,
            new AddCustomerContactInput("Rosa Sablan", "Spouse", Methods: [new AddContactMethodInput(ContactMethodKind.Mobile, "+1-670-285-1180")])));

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.CustomerContactCreated);

        Assert.Equal(AuditEntityTypes.CustomerContact, entry.EntityType);
        Assert.Equal(contact.Id.ToString(), entry.EntityId);
        Assert.Equal("auth0|cs-agent", entry.UserId);
        Assert.Null(entry.BeforeJson);

        // The methods ride along on the contact's own entry rather than being entities of their own
        // — the call AuditEntityTypes already makes for a meter's history and a bill's adjustments.
        // Matched without the leading "+": the audit serialiser's encoder escapes it to \u002B, and
        // a test asserting on the raw character would be asserting on the encoder, not the trail.
        Assert.Contains("670-285-1180", entry.AfterJson);
    }

    [Fact]
    public async Task An_update_audits_the_before_and_the_after()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        var contact = await host.WithContactsAsync(contacts =>
            contacts.AddAsync(customer.Id, new AddCustomerContactInput("Rosa Sablan", "Spouse")));

        await host.WithContactsAsync(contacts =>
            contacts.UpdateAsync(contact.Id, new UpdateCustomerContactInput("Rosa Sablan-Cruz", "Spouse", IsAuthorisedToDiscuss: false)));

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.CustomerContactUpdated);

        Assert.Contains("Rosa Sablan", entry.BeforeJson);
        Assert.Contains("Rosa Sablan-Cruz", entry.AfterJson);
    }

    [Fact]
    public async Task Authorising_a_contact_earns_an_entry_of_its_own()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        var contact = await host.WithContactsAsync(contacts =>
            contacts.AddAsync(customer.Id, new AddCustomerContactInput("Rosa Sablan", "Spouse")));

        await host.WithContactsAsync(contacts =>
            contacts.UpdateAsync(contact.Id, new UpdateCustomerContactInput("Rosa Sablan", "Spouse", IsAuthorisedToDiscuss: true)));

        await using var platform = host.NewPlatformContext();

        // Invariant 5: the sensitive act is permission-gated AND audited in its own right, so "who
        // was given the right to discuss this account" is a filter rather than a diff to read.
        var entry = await platform.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.CustomerContactAuthorised);

        Assert.Equal(AuditEntityTypes.CustomerContact, entry.EntityType);
        Assert.Equal(contact.Id.ToString(), entry.EntityId);
        Assert.Contains("true", entry.AfterJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_correction_that_leaves_the_flag_alone_earns_no_authorisation_entry()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        var contact = await host.WithContactsAsync(contacts =>
            contacts.AddAsync(customer.Id, new AddCustomerContactInput("Rosa Sablan", "Spouse")));

        await host.WithContactsAsync(contacts =>
            contacts.UpdateAsync(contact.Id, new UpdateCustomerContactInput("Rosa Sablan-Cruz", "Spouse", IsAuthorisedToDiscuss: false)));

        await using var platform = host.NewPlatformContext();

        Assert.False(await platform.AuditEntries.AnyAsync(candidate => candidate.Action == AuditActions.CustomerContactAuthorised));
    }

    [Fact]
    public async Task Marking_a_contact_authorised_without_the_permission_is_refused()
    {
        using var host = HostWithoutAuthorise();
        var customer = await ACustomerAsync(host);

        var contact = await host.WithContactsAsync(contacts =>
            contacts.AddAsync(customer.Id, new AddCustomerContactInput("Rosa Sablan", "Spouse")));

        var refusal = await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.WithContactsAsync(contacts =>
                contacts.UpdateAsync(contact.Id, new UpdateCustomerContactInput("Rosa Sablan", "Spouse", IsAuthorisedToDiscuss: true))));

        Assert.Contains(Permissions.Customers.Authorise, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_an_already_authorised_contact_without_the_permission_is_refused()
    {
        using var host = HostWithoutAuthorise();
        var customer = await ACustomerAsync(host);

        // The gate cannot live on the route: this is the same POST a clerk uses all day, and whether
        // it makes a disclosure decision depends on one field of the body.
        await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.WithContactsAsync(contacts => contacts.AddAsync(
                customer.Id,
                new AddCustomerContactInput("Rosa Sablan", "Spouse", IsAuthorisedToDiscuss: true))));

        await using var database = host.NewCustomersContext();

        Assert.Empty(await database.CustomerContacts.ToListAsync());
    }

    [Fact]
    public async Task A_rep_without_the_permission_may_still_correct_an_authorised_contact()
    {
        using var authorised = NewHost();
        var customer = await ACustomerAsync(authorised);

        var contact = await authorised.WithContactsAsync(contacts => contacts.AddAsync(
            customer.Id,
            new AddCustomerContactInput("Rosa Sablan", "Spouse", IsAuthorisedToDiscuss: true)));

        // Only a MOVE needs the permission. Refusing the spelling correction too would make the
        // narrower grant a broader one in practice — nobody could touch an authorised contact at all.
        await authorised.WithContactsAsync(contacts =>
            contacts.UpdateAsync(contact.Id, new UpdateCustomerContactInput("Rosa Sablan-Cruz", "Spouse", IsAuthorisedToDiscuss: true)));

        var updated = await authorised.WithContactsAsync(contacts => contacts.FindAsync(contact.Id));

        Assert.Equal("Rosa Sablan-Cruz", updated!.Name);
        Assert.True(updated.IsAuthorisedToDiscuss);
    }

    [Fact]
    public async Task Withdrawing_the_right_to_discuss_needs_the_permission_too()
    {
        using var host = HostWithoutAuthorise();
        var customer = await ACustomerAsync(host);

        // Written straight to the table, so the flag starts on without going through the gate this
        // test is about.
        var contact = CustomerContact.Add(customer.Id, "Rosa Sablan", "Spouse", Now);

        contact.SetAuthorisedToDiscuss(true);

        await using (var seed = host.NewCustomersContext())
        {
            seed.CustomerContacts.Add(contact);

            await seed.SaveChangesAsync();
        }

        // Taking the right away is as much a disclosure decision as granting it: a rep who could
        // silently withdraw it could lock a spouse out of an account without anybody signing it off.
        await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.WithContactsAsync(contacts =>
                contacts.UpdateAsync(contact.Id, new UpdateCustomerContactInput("Rosa Sablan", "Spouse", IsAuthorisedToDiscuss: false))));
    }

    [Fact]
    public async Task A_contact_against_a_customer_who_does_not_exist_is_not_found() =>
        await Assert.ThrowsAsync<CustomerNotFoundException>(async () =>
        {
            using var host = NewHost();

            await host.WithContactsAsync(contacts =>
                contacts.AddAsync(Guid.CreateVersion7(Now), new AddCustomerContactInput("Rosa Sablan")));
        });

    [Fact]
    public async Task A_contact_id_that_matches_nothing_is_not_found() =>
        await Assert.ThrowsAsync<CustomerContactNotFoundException>(async () =>
        {
            using var host = NewHost();

            await host.WithContactsAsync(contacts =>
                contacts.UpdateAsync(Guid.CreateVersion7(Now), new UpdateCustomerContactInput("Rosa Sablan", null, false)));
        });

    [Fact]
    public async Task A_refused_write_leaves_no_contact_and_no_audit_entry()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithContactsAsync(contacts => contacts.AddAsync(
                customer.Id,
                new AddCustomerContactInput(
                    "Rosa Sablan",
                    Methods:
                    [
                        new AddContactMethodInput(ContactMethodKind.Email, "rosa@example.com"),
                        new AddContactMethodInput(ContactMethodKind.Email, "rosa@example.com"),
                    ]))));

        await using var database = host.NewCustomersContext();
        await using var platform = host.NewPlatformContext();

        Assert.Empty(await database.CustomerContacts.ToListAsync());
        Assert.False(await platform.AuditEntries.AnyAsync(entry => entry.EntityType == AuditEntityTypes.CustomerContact));
    }

    [Fact]
    public async Task Promoting_a_method_demotes_the_other_in_the_same_transaction()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        var contact = await host.WithContactsAsync(contacts => contacts.AddAsync(
            customer.Id,
            new AddCustomerContactInput(
                "Rosa Sablan",
                Methods:
                [
                    new AddContactMethodInput(ContactMethodKind.Phone, "+1-670-532-0114"),
                    new AddContactMethodInput(ContactMethodKind.Phone, "+1-670-532-9987"),
                ])));

        var second = contact.Methods.Single(method => method.Value == "+1-670-532-9987");

        await host.WithContactsAsync(contacts => contacts.MakeMethodPrimaryAsync(contact.Id, second.Id));

        await using var database = host.NewCustomersContext();

        var stored = await database.CustomerContacts.Include(candidate => candidate.Methods).SingleAsync();

        // Read back off the database rather than off the returned aggregate: the demotion and the
        // promotion are two UPDATEs, and this is what says both were in the one SaveChanges. The
        // constraint is the aggregate's, not the schema's — see CustomerContactConfiguration for
        // why a filtered unique index cannot hold this rule under EF's update ordering.
        Assert.Single(stored.Methods, method => method.IsPrimary);
        Assert.Equal("+1-670-532-9987", stored.Methods.Single(method => method.IsPrimary).Value);
    }

    [Fact]
    public async Task Removing_a_contact_takes_its_methods_with_it_and_keeps_them_in_the_trail()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);

        var contact = await host.WithContactsAsync(contacts => contacts.AddAsync(
            customer.Id,
            new AddCustomerContactInput("Rosa Sablan", Methods: [new AddContactMethodInput(ContactMethodKind.Mobile, "+1-670-285-1180")])));

        await host.WithContactsAsync(async contacts =>
        {
            await contacts.RemoveAsync(contact.Id);

            return true;
        });

        await using var database = host.NewCustomersContext();
        await using var platform = host.NewPlatformContext();

        Assert.Empty(await database.CustomerContacts.ToListAsync());
        Assert.Empty(await database.Set<ContactMethod>().ToListAsync());

        var entry = await platform.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.CustomerContactRemoved);

        // "Who was on this account before" is exactly what a dispute asks, and a deleted row cannot
        // answer it — so the removal entry carries the contact as it stood.
        Assert.Contains("670-285-1180", entry.BeforeJson);
        Assert.Null(entry.AfterJson);
    }

    [Fact]
    public async Task The_list_is_one_customer_s_contacts_and_carries_their_methods()
    {
        using var host = NewHost();
        var customer = await ACustomerAsync(host);
        var other = await host.WithCustomersAsync(customers =>
            customers.RegisterAsync(new RegisterCustomerInput("Tinian Hardware", CustomerClass.Commercial)));

        await host.WithContactsAsync(contacts => contacts.AddAsync(
            customer.Id,
            new AddCustomerContactInput("Rosa Sablan", Methods: [new AddContactMethodInput(ContactMethodKind.Mobile, "+1-670-285-1180")])));

        await host.WithContactsAsync(contacts => contacts.AddAsync(other.Id, new AddCustomerContactInput("Jose Reyes")));

        var listed = await host.WithContactsAsync(contacts => contacts.ListAsync(customer.Id));

        var only = Assert.Single(listed);

        Assert.Equal("Rosa Sablan", only.Name);
        Assert.Single(only.Methods);
    }
}
