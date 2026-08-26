using GridCore.Contracts.Directories;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Notes;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Payments.Features.Payments;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// WP-2.13's cross-module reads against real Postgres: a note filed against a bill or a payment is
/// checked through <see cref="IBillDirectory"/> and <see cref="IPaymentDirectory"/>, and the
/// append-only log survives a round trip through a schema whose migration has been applied.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier proves everything that does not need infrastructure — the guards, the correction
/// rule, the pin, the audit trail, the ordering, and both directories against doubles, all in
/// milliseconds. What only containers can show is what the doubles were standing in for: that
/// Billing's and Payments' <i>real</i> directories answer the way the doubles claimed, so the rules
/// this module enforces against them hold against the schemas rather than against a dictionary.
/// </para>
/// <para>
/// The other thing that needs a database is the ordering. "Pinned first, then newest first" is a
/// Postgres <c>ORDER BY</c> over a partial index, and SQLite agreeing with it in the fast tier is
/// not the same claim.
/// </para>
/// <para>
/// <b>There is no event to wait for.</b> Notes publish nothing — nothing outside Customers acts on
/// one — so unlike <see cref="DepositLedgerTests"/> this file never touches the broker.
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CustomerNoteLogTests(GateFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// How many reading cycles a helper will run before it accepts that the meter is unreadable. See
    /// <see cref="AnIssuedBillAsync"/> — the simulator misses one read in twenty-five on purpose.
    /// </summary>
    private const int ReadAttempts = 5;

    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_logged_note_and_its_audit_entry_are_one_transaction()
    {
        // Two schemas, two contexts: the note in `customers`, the audit entry in `platform`. Either
        // both commit or neither does, and only a real connection can show that (invariant 1).
        var customer = await ARegisteredCustomerAsync();

        var note = await LogAsync(customer.Id, CustomerNoteKind.Complaint, "Unhappy about the disconnection notice.");

        await using var scope = fixture.CreateScope();

        var customers = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
        var stored = await customers.CustomerNotes.AsNoTracking().SingleAsync(row => row.Id == note.Id);

        Assert.Equal(CustomerNoteKind.Complaint, stored.Kind);
        Assert.Equal("Unhappy about the disconnection notice.", stored.Body);
        Assert.False(stored.IsPinned);

        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        Assert.True(await platform.AuditEntries.AnyAsync(entry =>
            entry.Action == AuditActions.CustomerNoteLogged && entry.EntityId == note.Id.ToString()));
    }

    [Fact]
    public async Task The_log_comes_back_pinned_first_then_newest_first_from_POSTGRES()
    {
        // The ordering as the database performs it, over the partial index the migration created.
        // The pinned note here is the OLDEST, so a sort that only ran on the date would put it last.
        var customer = await ARegisteredCustomerAsync();

        var oldest = await LogAsync(customer.Id, CustomerNoteKind.Note, "Dog on the property.");
        await LogAsync(customer.Id, CustomerNoteKind.InboundCall, "Rang about the reading.");
        var newest = await LogAsync(customer.Id, CustomerNoteKind.CounterVisit, "Came in to pay.");

        await PinAsync(oldest.Id, isPinned: true);

        var log = await ListAsync(customer.Id);

        Assert.Equal([oldest.Id, newest.Id], log.Take(2).Select(note => note.Id));
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public async Task The_pinned_only_filter_runs_over_the_partial_index()
    {
        var customer = await ARegisteredCustomerAsync();

        var standing = await LogAsync(customer.Id, CustomerNoteKind.Note, "Do not ring before ten.");
        await LogAsync(customer.Id, CustomerNoteKind.InboundCall, "Rang about the reading.");

        await PinAsync(standing.Id, isPinned: true);

        var pinned = await ListAsync(customer.Id, new CustomerNoteFilter(PinnedOnly: true));

        Assert.Equal(standing.Id, Assert.Single(pinned).Id);
    }

    [Fact]
    public async Task A_correction_leaves_the_row_it_corrects_untouched_in_the_database()
    {
        // The package's central rule, asserted against the table rather than against an object in
        // memory: the self-referencing foreign key holds, and the original still says what it said.
        var customer = await ARegisteredCustomerAsync();

        var original = await LogAsync(customer.Id, CustomerNoteKind.OutboundCall, "No answer.");

        var correction = await InScopeAsync(notes => notes.CorrectAsync(
            original.Id,
            new CorrectCustomerNoteInput(CustomerNoteKind.OutboundCall, "Answered — test confirmed for Tuesday.")));

        await using var scope = fixture.CreateScope();
        var customers = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();

        var stored = await customers.CustomerNotes
            .AsNoTracking()
            .Where(note => note.CustomerId == customer.Id)
            .OrderBy(note => note.Id)
            .ToListAsync();

        Assert.Equal(2, stored.Count);
        Assert.Equal("No answer.", stored[0].Body);
        Assert.Null(stored[0].CorrectsNoteId);
        Assert.Equal(original.Id, stored[1].CorrectsNoteId);
        Assert.Equal(correction.Id, stored[1].Id);
    }

    [Fact]
    public async Task A_note_linked_to_a_bill_is_verified_through_BILLINGS_OWN_directory()
    {
        // THE WORK PACKAGE'S GATE-TIER CASE. Nothing here stands a double in front of Billing: the
        // bill is raised by Billing's own service, and Customers confirms it through the Contracts
        // seam that Billing registers — which is the claim `FakeBillDirectory` can only assert about
        // itself.
        var bill = await AnIssuedBillAsync();

        var note = await InScopeAsync(notes => notes.LogAsync(
            bill.CustomerId,
            new LogCustomerNoteInput(
                CustomerNoteKind.BillingDispute,
                "Disputes the consumption on this bill.",
                Link: new CustomerNoteLinkInput(CustomerNoteLinkKind.Bill, bill.Id))));

        Assert.Equal(CustomerNoteLinkKind.Bill, note.LinkKind);
        Assert.Equal(bill.Id, note.LinkedEntityId);

        // Captured from the register that verified it, so the note reads without a cross-module
        // lookup years from now.
        Assert.Equal(bill.BillNumber, note.LinkedReference);
    }

    [Fact]
    public async Task A_note_linked_to_a_payment_is_verified_through_the_NEW_seam_over_real_rows()
    {
        // WP-2.13's addition to Contracts, answered by Payments' own PaymentDirectory over the
        // payments schema. The payment is taken through Payments' service, so the row is one the
        // module actually produced rather than one a test invented.
        var bill = await AnIssuedBillAsync();

        var taken = await InPaymentScopeAsync(register =>
            register.TakeAsync(new TakePaymentInput(bill.Id, 10.00m, PaymentMethods.Cash, null)));

        var note = await InScopeAsync(notes => notes.LogAsync(
            bill.CustomerId,
            new LogCustomerNoteInput(
                CustomerNoteKind.InboundCall,
                "Queried this payment.",
                Link: new CustomerNoteLinkInput(CustomerNoteLinkKind.Payment, taken.Payment.Id))));

        Assert.Equal(CustomerNoteLinkKind.Payment, note.LinkKind);
        Assert.Equal(taken.Payment.PaymentNumber, note.LinkedReference);
    }

    [Fact]
    public async Task A_note_linked_to_another_customers_bill_is_refused_by_the_REAL_directory()
    {
        // Failure path, and the one a double could agree with while the real seam disagreed: the
        // ownership check reads `BillSummary.CustomerId`, which Billing populates from its own rows.
        var bill = await AnIssuedBillAsync();
        var somebodyElse = await ARegisteredCustomerAsync();

        var refused = await Assert.ThrowsAsync<RegistryValidationException>(() => InScopeAsync(notes =>
            notes.LogAsync(
                somebodyElse.Id,
                new LogCustomerNoteInput(
                    CustomerNoteKind.BillingDispute,
                    "Wrong customer.",
                    Link: new CustomerNoteLinkInput(CustomerNoteLinkKind.Bill, bill.Id)))));

        Assert.Contains("belongs to another customer", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_work_order_link_is_stored_UNVERIFIED_until_WP_3_1_builds_that_register()
    {
        // WP-2.13's accepted gap, agreed with the owner and recorded in DECISIONS.md, asserted here
        // as well as in the fast tier: there is no IWorkOrderDirectory to ask, so the identifier is
        // stored as given and carries no reference. The day this starts failing is the day WP-3.1 has
        // closed the gap — which is exactly when somebody should come and read this comment.
        var customer = await ARegisteredCustomerAsync();
        var workOrderId = Guid.CreateVersion7();

        var note = await InScopeAsync(notes => notes.LogAsync(
            customer.Id,
            new LogCustomerNoteInput(
                CustomerNoteKind.FieldVisit,
                "Crew attended after the storm.",
                Link: new CustomerNoteLinkInput(CustomerNoteLinkKind.WorkOrder, workOrderId))));

        Assert.Equal(workOrderId, note.LinkedEntityId);
        Assert.Null(note.LinkedReference);

        await using var scope = fixture.CreateScope();
        var customers = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();

        // No foreign key stopped it, which is the point: a constraint across a module boundary is the
        // coupling schema-per-module exists to prevent, so nothing here depends on a workorders
        // schema existing at all.
        Assert.Equal(
            workOrderId,
            (await customers.CustomerNotes.AsNoTracking().SingleAsync(row => row.Id == note.Id)).LinkedEntityId);
    }

    private async Task<Customer> ARegisteredCustomerAsync()
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ICustomerService>()
            .RegisterAsync(new RegisterCustomerInput(
                $"Note customer {Guid.NewGuid().ToString("N")[..6]}",
                CustomerClass.Residential,
                "Ana Reyes"));
    }

    /// <summary>
    /// An issued bill on an energised account, assembled through four modules' own services exactly
    /// as the application would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape <see cref="DepositLedgerTests"/> and <see cref="PaymentRegistryTests"/> both use,
    /// and copied here for the same reason they copy it from each other: a note filed against a bill
    /// can only be tested against a bill the billing module actually raised. A shared helper would
    /// have to live in the fixture and would then be a fixture that knows about four modules'
    /// services — which is a bigger coupling than a repeated twenty lines in three gate tests.
    /// </para>
    /// <para>
    /// <b>The cycle is re-run until the meter is actually read, and that is not defensive coding.</b>
    /// The simulator misses one read in twenty-five <i>by design</i>
    /// (<c>SimulatedMeterReadingProvider.MissingReadChance</c> is <c>0.04</c>) — "no access to the
    /// meter" is a real thing that happens on a route, and modelling it is the whole point of the
    /// provider seam. The outcome is keyed on the seed, the cycle code and the meter number, so a
    /// helper that runs one cycle and asserts a bill came out of it fails about four times in a
    /// hundred for a reason with nothing to do with what is under test. Each attempt uses a fresh
    /// cycle code, which is a fresh draw; five of them miss with probability about one in ten
    /// million.
    /// </para>
    /// <para>
    /// The other two gate classes that build a bill this way do <b>not</b> do this, and are flaky at
    /// that rate — noted in STATUS.md rather than fixed here, since they belong to WP-2.5 and WP-2.12.
    /// </para>
    /// </remarks>
    private async Task<Bill> AnIssuedBillAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];

        Guid premise;
        Guid customer;

        await using (var scope = fixture.CreateScope())
        {
            customer = (await scope.ServiceProvider.GetRequiredService<ICustomerService>()
                .RegisterAsync(new RegisterCustomerInput($"Note customer {tag}", CustomerClass.Residential, "Ana Reyes")))
                .Id;

            premise = (await scope.ServiceProvider.GetRequiredService<IServiceLocationService>()
                .RegisterAsync(new ServiceLocationInput(
                    Address.Create($"{tag} As Nieves Road", "Songsong", "Rota", "MP", postalCode: "96951"),
                    "Meter on the north wall",
                    IsActive: true,
                    null)))
                .Id;
        }

        await using (var scope = fixture.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();
            var account = await accounts.OpenAsync(new OpenServiceAccountInput(customer, premise, "Requested at the counter"));

            await accounts.StartServiceAsync(account.Id, "Connected.");
        }

        await using (var scope = fixture.CreateScope())
        {
            var meters = scope.ServiceProvider.GetRequiredService<IMeterService>();
            var meter = await meters.RegisterAsync(new RegisterMeterInput($"SN-{tag}", MeterType.SinglePhase, Manufacturer: "Sensus"));

            await meters.AssignAsync(meter.Meter.Id, new AssignMeterInput(premise, 1_000.000m));

            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RecordAsync(meter.Meter.Id, new RecordReadingInput(1_600.000m, Note: "Read off the card"));
        }

        for (var attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            var cycle = $"NOTE-{tag}-{attempt}";

            await using (var scope = fixture.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                    .RunCycleAsync(new RunReadingCycleInput(cycle, Seed: 4471 + attempt));
            }

            await using (var scope = fixture.CreateScope())
            {
                var bills = scope.ServiceProvider.GetRequiredService<IBillService>();
                var run = await bills.RunAsync(new RunBillingInput(cycle));

                // Empty means the simulator refused this meter on this cycle — the modelled missed
                // read. Draw again rather than fail a test about notes over it.
                if (run.Bills.Count is 0)
                {
                    continue;
                }

                return await bills.IssueAsync(Assert.Single(run.Bills).Id, new IssueBillInput());
            }
        }

        Assert.Fail($"The simulator refused meter SN-{tag} on {ReadAttempts} consecutive cycles, which is not a thing that happens.");

        return null!;
    }

    private Task<CustomerNote> LogAsync(Guid customerId, CustomerNoteKind kind, string body) =>
        InScopeAsync(notes => notes.LogAsync(customerId, new LogCustomerNoteInput(kind, body)));

    private Task<CustomerNote> PinAsync(Guid noteId, bool isPinned) =>
        InScopeAsync(notes => notes.SetPinnedAsync(noteId, isPinned));

    private Task<IReadOnlyList<CustomerNote>> ListAsync(Guid customerId, CustomerNoteFilter? filter = null) =>
        InScopeAsync(notes => notes.ListAsync(customerId, filter));

    private async Task<TResult> InScopeAsync<TResult>(Func<ICustomerNoteService, Task<TResult>> work)
    {
        await using var scope = fixture.CreateScope();

        return await work(scope.ServiceProvider.GetRequiredService<ICustomerNoteService>());
    }

    private async Task<TResult> InPaymentScopeAsync<TResult>(Func<IPaymentService, Task<TResult>> work)
    {
        await using var scope = fixture.CreateScope();

        return await work(scope.ServiceProvider.GetRequiredService<IPaymentService>());
    }
}
