using GridCore.Contracts.Events;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Finance.Data;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Modules.Finance.Features.Journal;
using GridCore.Modules.Finance.Features.Reports;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// The event→journal seam against real Postgres and a real broker: a fact published by another
/// module crosses the bus, Finance posts it, and the entry is in <c>finance.journal_entries</c>
/// with its audit trail beside it.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier proves the accounting, the guards, the append-only rule, the idempotency and both
/// reports with no infrastructure at all — that is where nearly all of WP-2.6's cases live. What
/// only containers can show is that the ledger is genuinely written <i>by a consumer</i>: outside
/// any request, on the shared connection, in the same transaction as the dedupe claim and the audit
/// entry, in a Postgres schema whose migration has actually been applied.
/// </para>
/// <para>
/// The events are published directly rather than raised by working the revenue cycle. That walk
/// already has a gate test — <see cref="PaymentRegistryTests"/> takes a real payment against a real
/// bill and watches Finance's posting arrive — and WP-2.7 owns the end-to-end one. What is under
/// test here is the ledger at the far end, so the facts are stated plainly and the assertions are
/// about what Finance did with them.
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GeneralLedgerTests(GateFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// How long a test will wait for the broker to carry a fact to Finance. A ceiling, never a
    /// pause: every wait here returns the moment the thing it watches for happens.
    /// </summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_issued_bill_crosses_the_bus_and_becomes_a_journal_entry()
    {
        // The headline of the work package: the seam WP-0.5 proved with a log line now writes to a
        // ledger, with nothing upstream changed.
        var issued = ABill(184.55m);

        await PublishAsync(issued);
        await AwaitPostingAsync(issued.EventId);

        var entry = await EntryForAsync(issued.EventId);

        Assert.NotNull(entry);
        Assert.Equal(FinancePostings.BillIssuedSource, entry.Source);
        Assert.Equal(issued.BillNumber, entry.Reference);
        Assert.Equal(184.55m, entry.TotalDebits);
        Assert.Equal(entry.TotalDebits, entry.TotalCredits);
        Assert.Equal(issued.ServiceAccountId, entry.ServiceAccountId);

        Assert.Equal(
            184.55m,
            entry.Lines.Single(line => line.Account.Code == FinanceAccounts.AccountsReceivable).Debit);

        Assert.Equal(
            184.55m,
            entry.Lines.Single(line => line.Account.Code == FinanceAccounts.Revenue).Credit);
    }

    [Fact]
    public async Task A_posting_by_a_consumer_is_audited_against_the_system()
    {
        // INVARIANT 1 from outside a request, which is the half the fast tier cannot prove: a
        // consumer has no ICurrentUser to speak of, and it still leaves a trail.
        var issued = ABill(60m);

        await PublishAsync(issued);
        await AwaitPostingAsync(issued.EventId);

        var entry = await EntryForAsync(issued.EventId);

        Assert.NotNull(entry);

        await using var scope = fixture.CreateScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var audited = await platform.AuditEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(audit =>
                audit.Action == AuditActions.JournalPosted && audit.EntityId == entry.Id.ToString());

        Assert.NotNull(audited);
        Assert.Equal(AuditEntityTypes.JournalEntry, audited.EntityType);
        Assert.Equal("system", audited.UserId);
    }

    [Fact]
    public async Task A_bill_a_correction_and_a_payment_leave_a_trial_balance_that_nets_to_zero()
    {
        // All four consumers over one account, through the real broker, against real Postgres — and
        // the report that proves invariant 3 held the whole way.
        var serviceAccountId = Guid.CreateVersion7();
        var customerId = Guid.CreateVersion7();

        var issued = ABill(200m, serviceAccountId, customerId);
        var credited = ACredit(40m, serviceAccountId, customerId);
        var paid = APayment(100m, serviceAccountId, customerId);

        await PublishAsync(issued);
        await PublishAsync(credited);
        await PublishAsync(paid);

        await AwaitPostingAsync(issued.EventId);
        await AwaitPostingAsync(credited.EventId);
        await AwaitPostingAsync(paid.EventId);

        await using var scope = fixture.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<IFinanceReportService>();

        var trialBalance = await reports.TrialBalanceAsync();

        Assert.True(trialBalance.IsBalanced);
        Assert.Equal(0m, trialBalance.Difference);

        // 200 raised, 40 credited away, 100 paid — 60 still owed.
        var receivables = await reports.ReceivablesAsync(new ReceivablesQuery(ServiceAccountId: serviceAccountId));

        Assert.Equal(60m, receivables.TotalOutstanding);

        // And the subsidiary ledger agrees with its control account, which is the assertion that
        // keeps an AR view honest.
        Assert.Equal(
            trialBalance.Rows.Single(row => row.AccountCode == FinanceAccounts.AccountsReceivable).Balance,
            receivables.TotalOutstanding);
    }

    /// <summary>Publishes <paramref name="event"/> through the outbox, as its owning module would.</summary>
    /// <remarks>
    /// Generic, and it has to be: MassTransit routes on the <i>compile-time</i> type, so a helper
    /// taking <see cref="IIntegrationEvent"/> would publish every fact as the interface and no
    /// consumer would ever hear one.
    /// </remarks>
    private async Task PublishAsync<TEvent>(TEvent @event)
        where TEvent : class, IIntegrationEvent
    {
        await using var scope = fixture.CreateScope();

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        await unitOfWork.ExecuteAsync(token => publisher.PublishAsync(@event, token));
    }

    /// <summary>Waits for the journal entry raised by <paramref name="eventId"/> to appear.</summary>
    /// <remarks>
    /// The table is polled rather than the recorder awaited, and deliberately: the recorder's signal
    /// fires <i>inside</i> the consumer's transaction — that is what makes it a tap on the ledger
    /// rather than a substitute for it — so the row lands a moment after the signal does. This is
    /// still what CONVENTIONS.md rule G asks for: it returns the instant the commit lands and never
    /// sleeps a fixed span. <see cref="GateFixture.Postings"/> stays the right signal for a test
    /// that only cares that the seam fired.
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

    /// <summary>The journal entry raised for <paramref name="eventId"/>, if there is one yet.</summary>
    private async Task<JournalEntry?> EntryForAsync(Guid eventId)
    {
        await using var scope = fixture.CreateScope();

        var finance = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();

        return await finance.JournalEntries
            .AsNoTracking()
            .Include(entry => entry.Lines)
            .ThenInclude(line => line.Account)
            .FirstOrDefaultAsync(entry => entry.EventId == eventId);
    }

    private static BillIssued ABill(decimal amount, Guid? serviceAccountId = null, Guid? customerId = null) =>
        BillIssued.For(
            DateTimeOffset.UtcNow,
            billId: Guid.CreateVersion7(),
            billNumber: $"BIL-{Guid.NewGuid().ToString("N")[..6]}",
            serviceAccountId: serviceAccountId ?? Guid.CreateVersion7(),
            customerId: customerId ?? Guid.CreateVersion7(),
            periodStart: new DateOnly(2026, 7, 1),
            periodEnd: new DateOnly(2026, 7, 31),
            dueDate: new DateOnly(2026, 8, 20),
            amount: amount,
            currency: "USD");

    private static BillAdjusted ACredit(decimal amount, Guid serviceAccountId, Guid customerId) =>
        BillAdjusted.For(
            DateTimeOffset.UtcNow,
            billId: Guid.CreateVersion7(),
            billNumber: $"BIL-{Guid.NewGuid().ToString("N")[..6]}",
            serviceAccountId: serviceAccountId,
            customerId: customerId,
            adjustmentId: Guid.CreateVersion7(),
            kind: "Credit",
            amount: -amount,
            amountDue: 200m - amount,
            currency: "USD",
            reason: "Estimated read corrected after a site visit.");

    private static PaymentApproved APayment(decimal amount, Guid serviceAccountId, Guid customerId) =>
        PaymentApproved.For(
            DateTimeOffset.UtcNow,
            paymentId: Guid.CreateVersion7(),
            serviceAccountId: serviceAccountId,
            customerId: customerId,
            billId: Guid.CreateVersion7(),
            amount: amount,
            currency: "USD",
            method: "cash",
            providerReference: $"SIM-{Guid.NewGuid().ToString("N")[..6]}");
}
