using GridCore.Contracts.Events;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Finance.Data;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Modules.Finance.Features.Journal;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// WP-2.12's cross-module effect against real Postgres and a real broker: a deposit moved in
/// Customers crosses the bus and becomes a balanced journal entry in Finance.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier proves everything that does not need infrastructure — the arithmetic, the guards,
/// the permission gate, the audit trail, the three postings and the Billing consumer, all in
/// milliseconds. What only containers can show is the claim the work package actually makes: that a
/// deposit taken at a counter <b>reaches the general ledger</b>, through the outbox, in a schema
/// whose migration has been applied — and that the ledger row and the balance it moved were one
/// transaction on one connection.
/// </para>
/// <para>
/// This is the WP's named gate-tier case: "deposit event → journal entry".
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DepositLedgerTests(GateFixture fixture) : IAsyncLifetime
{
    /// <summary>How long a test will wait for the broker to carry a fact to Finance. A ceiling, never a pause.</summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Collecting_a_deposit_writes_the_ledger_and_the_balance_in_one_transaction()
    {
        // Two schemas and three contexts: the entry and the customer row in `customers`, the audit
        // entry and the outbox row in `platform`. Either all of it commits or none of it does, and
        // only a real connection can show that.
        var customer = await ARegisteredCustomerAsync();

        var entry = await CollectAsync(customer.Id, 75.00m);

        await using var scope = fixture.CreateScope();

        var customers = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();

        var stored = await customers.DepositEntries.AsNoTracking().SingleAsync(row => row.Id == entry.Id);

        Assert.Equal(DepositEntryKind.Collected, stored.Kind);
        Assert.Equal(75.00m, stored.Amount);
        Assert.Equal(75.00m, stored.BalanceAfter);

        // The projection moved with it. `numeric(18,2)` round-trips exactly — a float column would
        // hand back 74.99999999999999 and the difference would surface in a refund years later.
        Assert.Equal(
            75.00m,
            (await customers.Customers.AsNoTracking().SingleAsync(row => row.Id == customer.Id)).DepositHeld);
    }

    [Fact]
    public async Task A_collected_deposit_crosses_the_bus_and_becomes_a_liability_in_the_ledger()
    {
        // THE WORK PACKAGE'S GATE-TIER CASE. Nothing in this test touches Finance: Customers states
        // that money was taken, and the ledger entry appears because Finance is downstream of
        // everyone and consumed it.
        var customer = await ARegisteredCustomerAsync();

        await CollectAsync(customer.Id, 75.00m);

        var eventId = await AwaitPostedEventIdAsync(FinancePostings.DepositCollectedSource, customer.AccountNumber);
        var entry = await EntryForAsync(eventId);

        Assert.NotNull(entry);
        Assert.Equal(FinancePostings.DepositCollectedSource, entry.Source);
        Assert.Equal(customer.AccountNumber, entry.Reference);
        Assert.Equal(75.00m, entry.TotalDebits);
        Assert.Equal(entry.TotalDebits, entry.TotalCredits);

        Assert.Equal(75.00m, entry.Lines.Single(line => line.Account.Code == FinanceAccounts.Cash).Debit);
        Assert.Equal(75.00m, entry.Lines.Single(line => line.Account.Code == FinanceAccounts.CustomerDeposits).Credit);

        // A liability, never revenue: crediting revenue would inflate what the utility has earned by
        // every deposit on its books.
        Assert.Equal(
            AccountType.Liability,
            entry.Lines.Single(line => line.Account.Code == FinanceAccounts.CustomerDeposits).Account.Type);
    }

    [Fact]
    public async Task A_refund_posts_the_reverse_and_leaves_the_collection_exactly_as_it_was()
    {
        // Invariant 3 across the boundary: the correction is a NEW entry, and the original is still
        // in the ledger saying what it said.
        var customer = await ARegisteredCustomerAsync();

        await CollectAsync(customer.Id, 75.00m);

        var collectionId = await AwaitPostedEventIdAsync(FinancePostings.DepositCollectedSource, customer.AccountNumber);

        await RefundAsync(customer.Id, 75.00m);

        var refundId = await AwaitPostedEventIdAsync(FinancePostings.DepositRefundedSource, customer.AccountNumber);
        var reversal = await EntryForAsync(refundId);

        Assert.NotNull(reversal);
        Assert.Equal(FinancePostings.DepositRefundedSource, reversal.Source);
        Assert.Equal(75.00m, reversal.Lines.Single(line => line.Account.Code == FinanceAccounts.CustomerDeposits).Debit);
        Assert.Equal(75.00m, reversal.Lines.Single(line => line.Account.Code == FinanceAccounts.Cash).Credit);

        // Both entries stand, and the deposit account nets to nothing.
        var original = await EntryForAsync(collectionId);

        Assert.NotNull(original);
        Assert.Equal(75.00m, original.Lines.Single(line => line.Account.Code == FinanceAccounts.CustomerDeposits).Credit);
    }

    [Fact]
    public async Task Applying_a_deposit_settles_the_bill_and_relieves_the_receivable()
    {
        // The one event TWO modules claim, and the only place the whole path can be watched: Billing
        // reduces what the bill is owed under `billing.deposit-applied`, and Finance posts the
        // transfer under `finance.deposit-applied`. Neither suppresses the other, which is exactly
        // what the two distinct consumer names exist for.
        var bill = await AnIssuedBillAsync();

        // Half the bill, whatever the tariff made it. Pinning a figure here would make this test
        // fail the next time a rate plan moves, which is a fact about Billing and not about deposits.
        var part = decimal.Round(bill.Balance / 2, 2);

        await CollectAsync(bill.CustomerId, 75.00m);

        await ApplyAsync(bill.CustomerId, bill.Id, part);

        var eventId = await AwaitPostedEventIdAsync(FinancePostings.DepositAppliedSource, bill.BillNumber);
        var entry = await EntryForAsync(eventId);

        Assert.NotNull(entry);
        Assert.Equal(FinancePostings.DepositAppliedSource, entry.Source);
        Assert.Equal(part, entry.Lines.Single(line => line.Account.Code == FinanceAccounts.CustomerDeposits).Debit);
        Assert.Equal(part, entry.Lines.Single(line => line.Account.Code == FinanceAccounts.AccountsReceivable).Credit);

        // No cash line on either side: the money entered the utility when the deposit was taken.
        Assert.DoesNotContain(entry.Lines, line => line.Account.Code == FinanceAccounts.Cash);

        // The service account rides along, so an AR view can say whose debt went down.
        Assert.Equal(bill.ServiceAccountId, entry.ServiceAccountId);

        await AwaitBillSettlementAsync(bill.Id, part);

        await using var scope = fixture.CreateScope();

        var settled = await scope.ServiceProvider.GetRequiredService<BillingDbContext>()
            .Bills.AsNoTracking().Include(row => row.Adjustments).SingleAsync(row => row.Id == bill.Id);

        // Settled as money paid, never as an adjustment — WP-2.4's rule, which WORK_PACKAGES.md
        // restates for this package.
        Assert.Equal(part, settled.AmountPaid);
        Assert.Empty(settled.Adjustments);
    }

    private async Task<Customer> ARegisteredCustomerAsync()
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ICustomerService>()
            .RegisterAsync(new RegisterCustomerInput(
                $"Deposit customer {Guid.NewGuid().ToString("N")[..6]}",
                CustomerClass.Residential,
                "Ana Reyes"));
    }

    /// <summary>
    /// An issued bill on an energised account, assembled through four modules' own services exactly
    /// as the application would — the state an applied deposit arrives at. The shape
    /// <see cref="PaymentRegistryTests"/> already uses, for the same reason: a deposit can only
    /// settle a bill somebody actually owes.
    /// </summary>
    private async Task<Bill> AnIssuedBillAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var cycle = $"DEP-{tag}";

        Guid premise;
        Guid customer;

        await using (var scope = fixture.CreateScope())
        {
            customer = (await scope.ServiceProvider.GetRequiredService<ICustomerService>()
                .RegisterAsync(new RegisterCustomerInput($"Deposit customer {tag}", CustomerClass.Residential, "Ana Reyes")))
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

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RunCycleAsync(new RunReadingCycleInput(cycle, Seed: 4471));
        }

        await using (var scope = fixture.CreateScope())
        {
            var bills = scope.ServiceProvider.GetRequiredService<IBillService>();
            var draft = Assert.Single((await bills.RunAsync(new RunBillingInput(cycle))).Bills);

            return await bills.IssueAsync(draft.Id, new IssueBillInput());
        }
    }

    private async Task<DepositEntry> CollectAsync(Guid customerId, decimal amount)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ICustomerDepositService>()
            .CollectAsync(customerId, new CollectDepositInput(amount, Reason: "Taken at the counter."));
    }

    private async Task<DepositEntry> RefundAsync(Guid customerId, decimal amount)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ICustomerDepositService>()
            .RefundAsync(customerId, new RefundDepositInput(amount, "Account closed."));
    }

    private async Task<DepositEntry> ApplyAsync(Guid customerId, Guid billId, decimal amount)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ICustomerDepositService>()
            .ApplyAsync(customerId, new ApplyDepositInput(billId, amount, "Customer asked us to use the deposit."));
    }

    /// <summary>Waits for the journal entry raised by <paramref name="eventId"/> to appear.</summary>
    /// <remarks>
    /// The table is polled rather than a signal awaited, the call <see cref="GeneralLedgerTests"/>
    /// documents: it returns the instant the commit lands and never sleeps a fixed span, which is
    /// what CONVENTIONS.md rule G asks for.
    /// </remarks>
    private async Task AwaitPostingAsync(Guid eventId)
    {
        var deadline = DateTimeOffset.UtcNow + DeliveryTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await EntryForAsync(eventId) is not null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail($"Finance did not post event {eventId} within {DeliveryTimeout}.");
    }

    /// <summary>Waits for Billing's own consumer to reduce what the bill is owed.</summary>
    private async Task AwaitBillSettlementAsync(Guid billId, decimal expected)
    {
        var deadline = DateTimeOffset.UtcNow + DeliveryTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = fixture.CreateScope();

            var bill = await scope.ServiceProvider.GetRequiredService<BillingDbContext>()
                .Bills.AsNoTracking().FirstOrDefaultAsync(row => row.Id == billId);

            if (bill is not null && bill.AmountPaid == expected)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail($"Billing did not apply the deposit to bill {billId} within {DeliveryTimeout}.");
    }

    private async Task<JournalEntry?> EntryForAsync(Guid eventId)
    {
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<FinanceDbContext>()
            .JournalEntries
            .AsNoTracking()
            .Include(entry => entry.Lines)
            .ThenInclude(line => line.Account)
            .FirstOrDefaultAsync(entry => entry.EventId == eventId);
    }

    /// <summary>
    /// Waits for Finance's seam to fire for <paramref name="source"/> with <paramref name="reference"/>,
    /// and hands back the posting's event id.
    /// </summary>
    /// <remarks>
    /// The seam's own tap rather than a reconstruction of the event id: an id is stamped from the
    /// instant the movement happened, so a test that rebuilt it would be testing its own arithmetic.
    /// Polling here and not <c>NextAsync</c> because these tests fire several postings and care
    /// about a particular one — the recorder's list is what survives an ordering the broker chose.
    /// </remarks>
    private async Task<Guid> AwaitPostedEventIdAsync(string source, string reference)
    {
        var deadline = DateTimeOffset.UtcNow + DeliveryTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var posting = fixture.Postings.Postings
                .FirstOrDefault(candidate => candidate.Source == source && candidate.Reference == reference);

            if (posting is not null)
            {
                await AwaitPostingAsync(posting.EventId);

                return posting.EventId;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail($"Finance did not post a {source} entry for {reference} within {DeliveryTimeout}.");

        throw new InvalidOperationException("Unreachable.");
    }
}
