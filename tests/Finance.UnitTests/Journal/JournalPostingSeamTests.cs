using GridCore.Contracts.Events;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Modules.Finance.Features.Journal;
using GridCore.Modules.Finance.Features.Shared;
using GridCore.Modules.Finance.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Finance.UnitTests.Journal;

/// <summary>
/// The general ledger itself: an event becomes a balanced, audited, append-only journal entry.
/// Every one of these runs against the real EF model and the real unit of work on SQLite in-memory,
/// in milliseconds — CONVENTIONS.md rule C.
/// </summary>
public sealed class JournalPostingSeamTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_issued_bill_becomes_a_journal_entry_with_both_sides_of_the_posting()
    {
        using var host = new FinanceTestHost(new FakeClock(Now));

        var issued = ABill(184.55m);

        await host.PostAsync(FinancePostings.From(issued));

        await using var finance = host.NewFinanceContext();

        var entry = await finance.JournalEntries
            .Include(entry => entry.Lines)
            .ThenInclude(line => line.Account)
            .SingleAsync();

        Assert.Equal("JRN-000001", entry.EntryNumber);
        Assert.Equal(issued.EventId, entry.EventId);
        Assert.Equal(FinancePostings.BillIssuedSource, entry.Source);
        Assert.Equal(issued.BillNumber, entry.Reference);
        Assert.Equal("USD", entry.Currency);
        Assert.Equal(184.55m, entry.TotalDebits);
        Assert.Equal(184.55m, entry.TotalCredits);
        Assert.True(entry.IsBalanced);

        Assert.Equal(
            [FinanceAccounts.AccountsReceivable, FinanceAccounts.Revenue],
            entry.Lines.OrderBy(line => line.Sequence).Select(line => line.Account.Code));

        Assert.Equal(184.55m, entry.Lines.Single(line => line.Account.Code == FinanceAccounts.AccountsReceivable).Debit);
        Assert.Equal(184.55m, entry.Lines.Single(line => line.Account.Code == FinanceAccounts.Revenue).Credit);
    }

    [Fact]
    public async Task An_entry_carries_the_party_an_ar_view_is_built_from()
    {
        using var host = new FinanceTestHost(new FakeClock(Now));

        var issued = ABill(90m);

        await host.PostAsync(FinancePostings.From(issued));

        await using var finance = host.NewFinanceContext();
        var entry = await finance.JournalEntries.SingleAsync();

        Assert.Equal(issued.ServiceAccountId, entry.ServiceAccountId);
        Assert.Equal(issued.CustomerId, entry.CustomerId);
    }

    [Fact]
    public async Task A_goods_receipt_carries_no_party_because_its_subsidiary_is_a_vendor()
    {
        using var host = new FinanceTestHost(new FakeClock(Now));

        await host.PostAsync(FinancePostings.From(GoodsReceived.For(
            Now,
            receiptId: Guid.CreateVersion7(Now),
            purchaseOrderId: Guid.CreateVersion7(Now),
            warehouseId: Guid.CreateVersion7(Now),
            vendorId: Guid.CreateVersion7(Now),
            currency: "USD",
            lines: [new GoodsReceivedLine(Guid.CreateVersion7(Now), "TRF-100", 2m, 100m)])));

        await using var finance = host.NewFinanceContext();
        var entry = await finance.JournalEntries.SingleAsync();

        // A vendor is not a customer, and putting one in the field an AR view reads would put a
        // supplier on the receivables ledger. WP-4.1 owns the payables subsidiary.
        Assert.Null(entry.ServiceAccountId);
        Assert.Null(entry.CustomerId);
    }

    [Fact]
    public async Task Entry_numbers_continue_the_series()
    {
        using var host = new FinanceTestHost(new FakeClock(Now));

        await host.PostAsync(FinancePostings.From(ABill(10m)));
        await host.PostAsync(FinancePostings.From(ABill(20m)));
        await host.PostAsync(FinancePostings.From(ABill(30m)));

        await using var finance = host.NewFinanceContext();

        Assert.Equal(
            ["JRN-000001", "JRN-000002", "JRN-000003"],
            await finance.JournalEntries.OrderBy(entry => entry.EntryNumber).Select(entry => entry.EntryNumber).ToListAsync());
    }

    [Fact]
    public async Task Posting_leaves_an_audit_entry_against_the_system()
    {
        using var host = new FinanceTestHost(new FakeClock(Now));

        await host.PostAsync(FinancePostings.From(ABill(45m)));

        await using var finance = host.NewFinanceContext();
        await using var platform = host.NewPlatformContext();

        var entry = await finance.JournalEntries.SingleAsync();
        var audited = await platform.AuditEntries.SingleAsync();

        // INVARIANT 1. Attributed to `system`: a posting happens in a consumer, not at a keyboard.
        Assert.Equal(AuditActions.JournalPosted, audited.Action);
        Assert.Equal(AuditEntityTypes.JournalEntry, audited.EntityType);
        Assert.Equal(entry.Id.ToString(), audited.EntityId);
        Assert.Equal(SystemUser.SystemUserId, audited.UserId);
    }

    [Fact]
    public async Task An_entry_and_its_audit_entry_are_one_transaction()
    {
        // Rule C's whole point: two schemas, one connection, one transaction. A ledger that could
        // commit without its audit trail would break invariant 1 in the only way nobody notices.
        using var host = new FinanceTestHost(new FakeClock(Now));

        await host.PostAsync(FinancePostings.From(ABill(45m)));

        await using var finance = host.NewFinanceContext();
        await using var platform = host.NewPlatformContext();

        Assert.Equal(1, await finance.JournalEntries.CountAsync());
        Assert.Equal(1, await platform.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task A_redelivered_event_posts_once()
    {
        // THE FAILURE PATH THIS WORK PACKAGE IS ABOUT. A broker redelivers; without the claim the
        // idempotent handler takes, the same bill would be booked into revenue twice and the trial
        // balance would still balance — which is exactly why nobody would notice.
        using var host = new FinanceTestHost(new FakeClock(Now));

        var issued = ABill(120m);

        var first = await host.DeliverAsync(
            issued.EventId,
            BillIssuedConsumer.Name,
            (journal, token) => journal.PostAsync(FinancePostings.From(issued), token));

        var second = await host.DeliverAsync(
            issued.EventId,
            BillIssuedConsumer.Name,
            (journal, token) => journal.PostAsync(FinancePostings.From(issued), token));

        Assert.True(first);
        Assert.False(second);

        await using var finance = host.NewFinanceContext();

        Assert.Equal(1, await finance.JournalEntries.CountAsync());
        Assert.Equal(120m, await finance.JournalEntries.SumAsync(entry => entry.TotalDebits));
    }

    [Fact]
    public async Task Two_consumers_of_the_same_event_each_get_to_post()
    {
        // The other half of the dedupe rule: the claim is keyed on the consumer as well as the
        // event, which is why billing.payment-approved and finance.payment-approved coexist. Proven
        // here with two Finance names so the fast tier holds the rule without a second module.
        using var host = new FinanceTestHost(new FakeClock(Now));

        var approved = APayment(60m);

        await host.DeliverAsync(
            approved.EventId,
            PaymentApprovedConsumer.Name,
            (journal, token) => journal.PostAsync(FinancePostings.From(approved), token));

        var other = await host.DeliverAsync(
            approved.EventId,
            "finance.some-other-consumer",
            (_, _) => Task.CompletedTask);

        Assert.True(other);
    }

    [Fact]
    public async Task A_posting_to_an_account_the_chart_does_not_declare_is_refused()
    {
        // Failure path: the chart ships by migration, so a code that is not in it is a defect in the
        // mapping — and a journal entry is not the place to discover it.
        using var host = new FinanceTestHost(new FakeClock(Now));

        var thrown = await Assert.ThrowsAsync<FinanceValidationException>(() => host.PostAsync(
            JournalPostingIntent.For(
                Guid.CreateVersion7(Now),
                Now,
                "test.source",
                "REF-1",
                "A posting to an account nobody shipped",
                "USD",
                [
                    JournalLineIntent.Debits("9999", 10m),
                    JournalLineIntent.Credits(FinanceAccounts.Revenue, 10m),
                ])));

        Assert.Contains("9999", thrown.Message, StringComparison.Ordinal);

        await using var finance = host.NewFinanceContext();

        // And nothing was written: the whole posting is one transaction, so a refused entry leaves
        // no half of itself behind.
        Assert.Equal(0, await finance.JournalEntries.CountAsync());
        Assert.Equal(0, await finance.JournalLines.CountAsync());
    }

    [Fact]
    public async Task A_posted_entry_cannot_be_edited()
    {
        // INVARIANT 3. The ledger is append-only; a correction is a new entry, which is exactly what
        // a BillAdjusted posting is. The same guard PlatformDbContext puts on the audit trail.
        using var host = new FinanceTestHost(new FakeClock(Now));

        await host.PostAsync(FinancePostings.From(ABill(45m)));

        await using var finance = host.NewFinanceContext();

        var entry = await finance.JournalEntries.SingleAsync();

        finance.Entry(entry).Property(nameof(JournalEntry.Reference)).CurrentValue = "REWRITTEN";

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => finance.SaveChangesAsync());

        Assert.Contains("append-only", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_posted_entry_cannot_be_deleted()
    {
        using var host = new FinanceTestHost(new FakeClock(Now));

        await host.PostAsync(FinancePostings.From(ABill(45m)));

        await using var finance = host.NewFinanceContext();

        finance.JournalEntries.Remove(await finance.JournalEntries.SingleAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => finance.SaveChangesAsync());
    }

    [Fact]
    public async Task A_posted_line_cannot_be_edited()
    {
        // The entry's totals balance exactly as long as nobody has edited the lines under them.
        using var host = new FinanceTestHost(new FakeClock(Now));

        await host.PostAsync(FinancePostings.From(ABill(45m)));

        await using var finance = host.NewFinanceContext();

        var line = await finance.JournalLines.FirstAsync();

        finance.Entry(line).Property(nameof(JournalLine.Debit)).CurrentValue = 1_000_000m;

        await Assert.ThrowsAsync<InvalidOperationException>(() => finance.SaveChangesAsync());
    }

    [Fact]
    public async Task The_same_event_cannot_be_posted_twice_even_past_the_dedupe_claim()
    {
        // Defence in depth behind the claim: a consumer renamed, a claim table restored from an
        // older backup, and the ledger would double every posting silently. The unique index on
        // event_id is the database's own answer.
        using var host = new FinanceTestHost(new FakeClock(Now));

        var issued = ABill(75m);

        await host.PostAsync(FinancePostings.From(issued));

        await Assert.ThrowsAsync<DbUpdateException>(() => host.PostAsync(FinancePostings.From(issued)));
    }

    [Fact]
    public async Task An_entry_records_whoever_posted_it()
    {
        using var host = new FinanceTestHost(new FakeClock(Now), new FakeCurrentUser("kfoster", "Kim Foster"));

        await host.PostAsync(FinancePostings.From(ABill(45m)));

        await using var finance = host.NewFinanceContext();
        var entry = await finance.JournalEntries.SingleAsync();

        Assert.Equal("kfoster", entry.ActorId);
        Assert.Equal("Kim Foster", entry.ActorName);
    }

    [Fact]
    public async Task The_accounting_date_is_the_day_the_fact_happened_not_the_day_finance_heard_it()
    {
        // A redelivery replayed a week later must post to the day the bill was issued, or a demo
        // that reprocesses its outbox quietly moves last month's revenue into this month.
        var heardAt = Now.AddDays(7);

        using var host = new FinanceTestHost(new FakeClock(heardAt));

        var issued = ABill(45m);

        await host.PostAsync(FinancePostings.From(issued));

        await using var finance = host.NewFinanceContext();
        var entry = await finance.JournalEntries.SingleAsync();

        Assert.Equal(DateOnly.FromDateTime(Now.UtcDateTime), entry.PostedOn);
        Assert.Equal(Now, entry.OccurredAt);
        Assert.Equal(heardAt, entry.PostedAt);
    }

    private static BillIssued ABill(decimal amount) => BillIssued.For(
        Now,
        billId: Guid.CreateVersion7(Now),
        billNumber: $"BIL-{Random.Shared.Next(100000, 999999)}",
        serviceAccountId: Guid.CreateVersion7(Now),
        customerId: Guid.CreateVersion7(Now),
        periodStart: new DateOnly(2026, 7, 1),
        periodEnd: new DateOnly(2026, 7, 31),
        dueDate: new DateOnly(2026, 8, 20),
        amount: amount,
        currency: "USD");

    private static PaymentApproved APayment(decimal amount) => PaymentApproved.For(
        Now,
        paymentId: Guid.CreateVersion7(Now),
        serviceAccountId: Guid.CreateVersion7(Now),
        customerId: Guid.CreateVersion7(Now),
        billId: Guid.CreateVersion7(Now),
        amount: amount,
        currency: "USD",
        method: "card",
        providerReference: $"SIM-{Random.Shared.Next(100000, 999999)}");
}
