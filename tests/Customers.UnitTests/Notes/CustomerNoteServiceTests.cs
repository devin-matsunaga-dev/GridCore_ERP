using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Notes;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Notes;

/// <summary>
/// The note log over the schema: what reaches the database, what the audit trail says about it, and
/// the two link seams — <c>IBillDirectory</c> and the WP-2.13 <c>IPaymentDirectory</c> — answered by
/// doubles rather than by another module's tables.
/// </summary>
public class CustomerNoteServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 10, 15, 0, TimeSpan.Zero);

    private static CustomersTestHost NewHost(TimeProvider? clock = null) =>
        new(clock ?? new FakeClock(Now), new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    private static Task<Customer> ACustomer(CustomersTestHost host, string name = "Maria Santos") =>
        host.WithCustomersAsync(customers => customers.RegisterAsync(new RegisterCustomerInput(name, CustomerClass.Residential)));

    /// <summary>A customer with an open account at a premise — what a note about an account needs.</summary>
    private static async Task<(Customer Customer, ServiceAccount Account)> ACustomerWithAnAccount(CustomersTestHost host)
    {
        var customer = await ACustomer(host);

        var location = await host.WithLocationsAsync(locations => locations.RegisterAsync(
            new ServiceLocationInput(Address.Create("1 Songsong Road", "Songsong", "Rota", "MP", postalCode: "96951"), "House")));

        var account = await host.WithAccountsAsync(accounts => accounts.OpenAsync(
            new OpenServiceAccountInput(customer.Id, location.Id, ServiceType.Electricity)));

        return (customer, account);
    }

    private static Task<CustomerNote> Log(
        CustomersTestHost host,
        Guid customerId,
        CustomerNoteKind kind = CustomerNoteKind.InboundCall,
        string body = "Rang about the meter reading.",
        Guid? serviceAccountId = null,
        DateOnly? followUpOn = null,
        CustomerNoteLinkInput? link = null) =>
        host.WithNotesAsync(notes => notes.LogAsync(
            customerId,
            new LogCustomerNoteInput(kind, body, serviceAccountId, followUpOn, link)));

    [Fact]
    public async Task A_logged_note_is_stored_against_the_customer()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);

        var note = await Log(host, customer.Id, CustomerNoteKind.Complaint, "Unhappy about the notice.");

        await using var database = host.NewCustomersContext();
        var stored = await database.CustomerNotes.SingleAsync(row => row.Id == note.Id);

        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Equal(CustomerNoteKind.Complaint, stored.Kind);
        Assert.Equal("Unhappy about the notice.", stored.Body);
        Assert.Equal("auth0|cs-agent", stored.ActorId);
        Assert.Equal("Ana Cruz", stored.ActorName);
        Assert.Equal(Now, stored.RecordedAt);
    }

    [Fact]
    public async Task A_note_against_a_customer_who_does_not_exist_is_a_404()
    {
        using var host = NewHost();

        // A 404 rather than a foreign-key error at commit time — the call CustomerContactService
        // already makes about an orphan row nothing will ever read.
        await Assert.ThrowsAsync<CustomerNotFoundException>(() => Log(host, Guid.CreateVersion7()));
    }

    [Fact]
    public async Task A_note_can_be_filed_against_one_of_the_customers_accounts()
    {
        using var host = NewHost();
        var (customer, account) = await ACustomerWithAnAccount(host);

        var note = await Log(host, customer.Id, serviceAccountId: account.Id);

        Assert.Equal(account.Id, note.ServiceAccountId);
    }

    [Fact]
    public async Task A_note_filed_against_somebody_elses_account_is_refused()
    {
        using var host = NewHost();
        var (_, account) = await ACustomerWithAnAccount(host);
        var other = await ACustomer(host, "Jose Taimanao");

        // A disclosure, not a typo: the note would appear on the other customer's 360.
        var refused = await Assert.ThrowsAsync<RegistryValidationException>(() =>
            Log(host, other.Id, serviceAccountId: account.Id));

        Assert.Contains("is not this customer's", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_filed_against_an_account_that_does_not_exist_is_refused()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            Log(host, customer.Id, serviceAccountId: Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Logging_a_note_writes_an_audit_entry()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);

        var note = await Log(host, customer.Id);

        await using var platform = host.NewPlatformContext();
        var entry = await platform.AuditEntries.SingleAsync(row =>
            row.Action == AuditActions.CustomerNoteLogged && row.EntityId == note.Id.ToString());

        Assert.Equal(AuditEntityTypes.CustomerNote, entry.EntityType);

        // A null `before`, always: the row did not exist a moment ago.
        Assert.Null(entry.BeforeJson);
        Assert.Contains(note.Body, entry.AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_note_log_publishes_nothing()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);

        var before = host.Events.Published.Count;

        await Log(host, customer.Id);

        // Nothing outside Customers acts on a note — the call WP-2.11 made about contacts. Publishing
        // would be inventing a consumer, and the assertion is here so that adding one is a decision
        // rather than a side effect.
        Assert.Equal(before, host.Events.Published.Count);
    }

    // ---- Links -------------------------------------------------------------------------------

    [Fact]
    public async Task A_note_linked_to_a_bill_captures_the_bill_number_through_the_seam()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var bill = host.Bills.Add(customer.Id);

        var note = await Log(host, customer.Id, CustomerNoteKind.BillingDispute, "Disputes the consumption.",
            link: new CustomerNoteLinkInput(CustomerNoteLinkKind.Bill, bill.Id));

        Assert.Equal(CustomerNoteLinkKind.Bill, note.LinkKind);
        Assert.Equal(bill.Id, note.LinkedEntityId);

        // Stored beside the id, so the note reads without a cross-module lookup two years from now.
        Assert.Equal(bill.BillNumber, note.LinkedReference);
        Assert.Contains(bill.Id, host.Bills.Lookups);
    }

    [Fact]
    public async Task A_note_linked_to_a_nonexistent_bill_is_refused()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);

        var refused = await Assert.ThrowsAsync<RegistryValidationException>(() =>
            Log(host, customer.Id, link: new CustomerNoteLinkInput(CustomerNoteLinkKind.Bill, Guid.CreateVersion7())));

        // A 400, not a 404: the thing that was not found is a field of the body, not the customer in
        // the URL.
        Assert.Contains("was not found", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_linked_to_another_customers_bill_is_refused()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var other = await ACustomer(host, "Jose Taimanao");
        var bill = host.Bills.Add(other.Id);

        var refused = await Assert.ThrowsAsync<RegistryValidationException>(() =>
            Log(host, customer.Id, link: new CustomerNoteLinkInput(CustomerNoteLinkKind.Bill, bill.Id)));

        Assert.Contains("belongs to another customer", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_linked_to_a_payment_captures_the_payment_number_through_the_new_seam()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var payment = host.Payments.Add(customer.Id);

        var note = await Log(host, customer.Id, link: new CustomerNoteLinkInput(CustomerNoteLinkKind.Payment, payment.Id));

        Assert.Equal(CustomerNoteLinkKind.Payment, note.LinkKind);
        Assert.Equal(payment.Id, note.LinkedEntityId);
        Assert.Equal(payment.PaymentNumber, note.LinkedReference);
        Assert.Contains(payment.Id, host.Payments.Lookups);
    }

    [Fact]
    public async Task A_note_linked_to_a_nonexistent_payment_is_refused()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            Log(host, customer.Id, link: new CustomerNoteLinkInput(CustomerNoteLinkKind.Payment, Guid.CreateVersion7())));
    }

    [Fact]
    public async Task A_note_linked_to_another_customers_payment_is_refused()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var other = await ACustomer(host, "Jose Taimanao");
        var payment = host.Payments.Add(other.Id);

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            Log(host, customer.Id, link: new CustomerNoteLinkInput(CustomerNoteLinkKind.Payment, payment.Id)));
    }

    [Fact]
    public async Task A_note_can_be_filed_against_a_DECLINED_payment()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var declined = host.Payments.Add(customer.Id, status: "Declined");

        // Usually the reason the customer rang. Only existence and ownership are checked; the seam is
        // narrow on purpose.
        var note = await Log(host, customer.Id, link: new CustomerNoteLinkInput(CustomerNoteLinkKind.Payment, declined.Id));

        Assert.Equal(declined.Id, note.LinkedEntityId);
    }

    [Fact]
    public async Task A_work_order_link_is_stored_UNVERIFIED_and_carries_no_reference()
    {
        // WP-2.13's one accepted gap, agreed with the owner and recorded in DECISIONS.md: the
        // WorkOrders module is a stub until WP-3.1, so there is no register to ask and no
        // IWorkOrderDirectory to ask it through. The shape ships now; the guarantee arrives with the
        // seam. This test is what says so out loud, so the day it starts failing is the day WP-3.1
        // has closed the gap.
        using var host = NewHost();
        var customer = await ACustomer(host);
        var workOrderId = Guid.CreateVersion7();

        var note = await Log(host, customer.Id, link: new CustomerNoteLinkInput(CustomerNoteLinkKind.WorkOrder, workOrderId));

        Assert.Equal(CustomerNoteLinkKind.WorkOrder, note.LinkKind);
        Assert.Equal(workOrderId, note.LinkedEntityId);

        // No reference: there is no register to ask what the number is, and inventing one would put a
        // number on screen that nothing produced.
        Assert.Null(note.LinkedReference);
    }

    [Fact]
    public void The_link_kinds_GridCore_can_verify_are_exactly_the_two_with_a_seam() =>
        // Pinned as a list rather than derived, so WP-3.1 adding IWorkOrderDirectory has to come here
        // and say so — which is the point of writing the exception down as code.
        Assert.Equal(
            [CustomerNoteLinkKind.Bill, CustomerNoteLinkKind.Payment],
            CustomerNoteLinkKinds.All.Where(CustomerNoteLinkKinds.IsVerifiable));

    // ---- Corrections -------------------------------------------------------------------------

    [Fact]
    public async Task A_correction_adds_a_row_and_leaves_the_original_untouched()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var original = await Log(host, customer.Id, body: "No answer.");

        var correction = await host.WithNotesAsync(notes => notes.CorrectAsync(
            original.Id,
            new CorrectCustomerNoteInput(CustomerNoteKind.OutboundCall, "Answered — test confirmed.")));

        await using var database = host.NewCustomersContext();

        Assert.Equal(2, await database.CustomerNotes.CountAsync(note => note.CustomerId == customer.Id));
        Assert.Equal("No answer.", (await database.CustomerNotes.SingleAsync(note => note.Id == original.Id)).Body);
        Assert.Equal(original.Id, correction.CorrectsNoteId);
    }

    [Fact]
    public async Task Correcting_a_note_that_does_not_exist_is_a_404()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<CustomerNoteNotFoundException>(() => host.WithNotesAsync(notes =>
            notes.CorrectAsync(Guid.CreateVersion7(), new CorrectCustomerNoteInput(CustomerNoteKind.Note, "Anything."))));
    }

    [Fact]
    public async Task A_correction_is_audited_against_the_NEW_note_with_the_old_one_as_its_before()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var original = await Log(host, customer.Id, body: "No answer.");

        var correction = await host.WithNotesAsync(notes => notes.CorrectAsync(
            original.Id,
            new CorrectCustomerNoteInput(CustomerNoteKind.OutboundCall, "Answered.")));

        await using var platform = host.NewPlatformContext();
        var entry = await platform.AuditEntries.SingleAsync(row => row.Action == AuditActions.CustomerNoteCorrected);

        // Against the correction, because that is the row that came into existence. Claiming an
        // entity was updated would make the trail disagree with the table.
        Assert.Equal(correction.Id.ToString(), entry.EntityId);
        Assert.Contains("No answer.", entry.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains("Answered.", entry.AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_correction_can_change_the_link_as_well_as_the_words()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var right = host.Bills.Add(customer.Id);
        var wrong = host.Bills.Add(customer.Id);

        var original = await Log(host, customer.Id, CustomerNoteKind.BillingDispute, "Disputes it.",
            link: new CustomerNoteLinkInput(CustomerNoteLinkKind.Bill, wrong.Id));

        var correction = await host.WithNotesAsync(notes => notes.CorrectAsync(
            original.Id,
            new CorrectCustomerNoteInput(
                CustomerNoteKind.BillingDispute,
                "Disputes it — the earlier note named the wrong bill.",
                Link: new CustomerNoteLinkInput(CustomerNoteLinkKind.Bill, right.Id))));

        // The commonest correction there is, and it is verified exactly as the original was.
        Assert.Equal(right.Id, correction.LinkedEntityId);
        Assert.Equal(right.BillNumber, correction.LinkedReference);
        Assert.Equal(wrong.Id, original.LinkedEntityId);
    }

    [Fact]
    public async Task A_corrections_link_is_checked_against_the_ORIGINALS_customer()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var other = await ACustomer(host, "Jose Taimanao");
        var theirBill = host.Bills.Add(other.Id);

        var original = await Log(host, customer.Id);

        // A correction is filed where the note it corrects was filed, so the ownership check has to
        // use that customer and not one taken from the request.
        await Assert.ThrowsAsync<RegistryValidationException>(() => host.WithNotesAsync(notes =>
            notes.CorrectAsync(
                original.Id,
                new CorrectCustomerNoteInput(
                    CustomerNoteKind.Note,
                    "Wrong.",
                    Link: new CustomerNoteLinkInput(CustomerNoteLinkKind.Bill, theirBill.Id)))));
    }

    // ---- Pinning -----------------------------------------------------------------------------

    [Fact]
    public async Task Pinning_a_note_is_stored_and_audited()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var note = await Log(host, customer.Id);

        var pinned = await host.WithNotesAsync(notes => notes.SetPinnedAsync(note.Id, true));

        Assert.True(pinned.IsPinned);

        await using var database = host.NewCustomersContext();
        Assert.True((await database.CustomerNotes.SingleAsync(row => row.Id == note.Id)).IsPinned);

        await using var platform = host.NewPlatformContext();
        var entry = await platform.AuditEntries.SingleAsync(row => row.Action == AuditActions.CustomerNotePinned);

        Assert.Equal(note.Id.ToString(), entry.EntityId);
        Assert.Contains("\"isPinned\":false", entry.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains("\"isPinned\":true", entry.AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pinning_a_pinned_note_is_neither_a_conflict_nor_a_second_audit_entry()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var note = await Log(host, customer.Id);

        await host.WithNotesAsync(notes => notes.SetPinnedAsync(note.Id, true));
        var again = await host.WithNotesAsync(notes => notes.SetPinnedAsync(note.Id, true));

        Assert.True(again.IsPinned);

        await using var platform = host.NewPlatformContext();

        // An entry saying the flag went from true to true is noise in the one place noise is most
        // expensive.
        Assert.Equal(1, await platform.AuditEntries.CountAsync(row => row.Action == AuditActions.CustomerNotePinned));
    }

    [Fact]
    public async Task Pinning_a_note_that_does_not_exist_is_a_404()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<CustomerNoteNotFoundException>(() =>
            host.WithNotesAsync(notes => notes.SetPinnedAsync(Guid.CreateVersion7(), true)));
    }

    // ---- Reading -----------------------------------------------------------------------------

    [Fact]
    public async Task The_log_comes_back_pinned_first_then_newest_first()
    {
        // WORK_PACKAGES.md: "pinned notes sort ahead of unpinned regardless of date". The pinned note
        // here is the OLDEST, so a sort that only ran on the date would put it last.
        var clock = new FakeClock(Now);
        using var host = NewHost(clock);
        var customer = await ACustomer(host);

        var oldest = await Log(host, customer.Id, body: "Oldest.");
        clock.Advance(TimeSpan.FromHours(1));
        await Log(host, customer.Id, body: "Middle.");
        clock.Advance(TimeSpan.FromHours(1));
        var newest = await Log(host, customer.Id, body: "Newest.");

        await host.WithNotesAsync(notes => notes.SetPinnedAsync(oldest.Id, true));

        var log = await host.WithNotesAsync(notes => notes.ListAsync(customer.Id));

        Assert.Equal([oldest.Id, newest.Id], log.Take(2).Select(note => note.Id));
        Assert.Equal("Middle.", log[2].Body);
    }

    [Fact]
    public async Task The_log_of_a_customer_who_does_not_exist_is_a_404() =>
        // Not an empty list: "this customer has no notes" and "there is no such customer" are
        // different answers, and a screen handed the first for the second renders an empty state
        // under a name nobody has.
        await Assert.ThrowsAsync<CustomerNotFoundException>(async () =>
        {
            using var host = NewHost();

            await host.WithNotesAsync(notes => notes.ListAsync(Guid.CreateVersion7()));
        });

    [Fact]
    public async Task A_customer_with_no_notes_reads_back_as_an_empty_log()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);

        Assert.Empty(await host.WithNotesAsync(notes => notes.ListAsync(customer.Id)));
    }

    [Fact]
    public async Task The_log_holds_only_this_customers_notes()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var other = await ACustomer(host, "Jose Taimanao");

        await Log(host, customer.Id, body: "Theirs.");
        await Log(host, other.Id, body: "Somebody else's.");

        var log = await host.WithNotesAsync(notes => notes.ListAsync(customer.Id));

        Assert.Equal("Theirs.", Assert.Single(log).Body);
    }

    [Fact]
    public async Task The_log_narrows_by_kind_by_account_and_to_the_pinned()
    {
        var clock = new FakeClock(Now);
        using var host = NewHost(clock);
        var (customer, account) = await ACustomerWithAnAccount(host);

        var call = await Log(host, customer.Id, CustomerNoteKind.InboundCall, "Rang.");
        clock.Advance(TimeSpan.FromMinutes(1));
        var standing = await Log(host, customer.Id, CustomerNoteKind.Note, "Dog on the property.");
        clock.Advance(TimeSpan.FromMinutes(1));
        var aboutTheAccount = await Log(host, customer.Id, CustomerNoteKind.Complaint, "Notice arrived late.", serviceAccountId: account.Id);

        await host.WithNotesAsync(notes => notes.SetPinnedAsync(standing.Id, true));

        Assert.Equal(
            call.Id,
            Assert.Single(await host.WithNotesAsync(notes =>
                notes.ListAsync(customer.Id, new CustomerNoteFilter(Kind: CustomerNoteKind.InboundCall)))).Id);

        Assert.Equal(
            aboutTheAccount.Id,
            Assert.Single(await host.WithNotesAsync(notes =>
                notes.ListAsync(customer.Id, new CustomerNoteFilter(ServiceAccountId: account.Id)))).Id);

        Assert.Equal(
            standing.Id,
            Assert.Single(await host.WithNotesAsync(notes =>
                notes.ListAsync(customer.Id, new CustomerNoteFilter(PinnedOnly: true)))).Id);
    }

    [Fact]
    public async Task A_limit_beyond_what_the_service_allows_is_clamped_rather_than_obeyed()
    {
        var clock = new FakeClock(Now);
        using var host = NewHost(clock);
        var customer = await ACustomer(host);

        for (var i = 0; i < 3; i++)
        {
            await Log(host, customer.Id, body: $"Note {i}.");
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        // A caller asking for nothing, or for everything, gets a window rather than a 400 — the same
        // call every paged read in this module makes.
        Assert.Single(await host.WithNotesAsync(notes => notes.ListAsync(customer.Id, new CustomerNoteFilter(Limit: 1))));
        Assert.Single(await host.WithNotesAsync(notes => notes.ListAsync(customer.Id, new CustomerNoteFilter(Limit: 0))));
        Assert.Equal(3, (await host.WithNotesAsync(notes => notes.ListAsync(customer.Id, new CustomerNoteFilter(Limit: int.MaxValue)))).Count);
    }

    [Fact]
    public async Task One_note_can_be_read_by_its_own_id()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var note = await Log(host, customer.Id);

        Assert.Equal(note.Id, (await host.WithNotesAsync(notes => notes.FindAsync(note.Id)))!.Id);
        Assert.Null(await host.WithNotesAsync(notes => notes.FindAsync(Guid.CreateVersion7())));
    }

    [Fact]
    public async Task A_note_records_whichever_rep_actually_wrote_it()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);

        var supervisor = new FakeCurrentUser("auth0|supervisor", "Jo Reyes");

        var note = await host.AsAsync(supervisor, notes => notes.LogAsync(
            customer.Id,
            new LogCustomerNoteInput(CustomerNoteKind.Note, "Reviewed the account.")));

        // A service record is read back on the phone to a customer, so the name is captured beside
        // the id rather than resolved against the identity provider years later.
        Assert.Equal("auth0|supervisor", note.ActorId);
        Assert.Equal("Jo Reyes", note.ActorName);
    }
}
