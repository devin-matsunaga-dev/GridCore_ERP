using GridCore.Contracts.Events;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Modules.Billing.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Billing.UnitTests.Bills;

/// <summary>
/// Billing's half of WP-2.12: what a customer's applied security deposit does to a bill.
/// </summary>
/// <remarks>
/// Customers states that the deposit moved and never touches a bill; this module decides what that
/// means for the document. The shape and the reasoning are <see cref="BillPaymentTests"/>' — the
/// two things that settle a bill go through one aggregate method — and the point of this suite is
/// the two ways a deposit differs: it leaves the adjustment trail alone, and it is audited as its
/// own fact.
/// </remarks>
public sealed class BillDepositTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeClock _clock = new(Now);
    private readonly BillingTestHost _host;

    /// <remarks>
    /// No explicit current user: a consumer runs outside any request, so the entry a deposit leaves
    /// on a bill is attributed to the system. The rep who decided to spend the deposit is named on
    /// the deposit ledger's own entry, in the Customers schema.
    /// </remarks>
    public BillDepositTests() => _host = new BillingTestHost(_clock);

    public void Dispose() => _host.Dispose();

    private async Task<Bill> AnIssuedBillAsync()
    {
        var location = Guid.CreateVersion7(Now);

        _host.Accounts.Add(location);
        _host.Readings.Add(location, consumption: 400m, cycleCode: "2026-07", readingDate: Now.AddDays(-20));

        var run = await _host.WithBillsAsync(register => register.RunAsync(new RunBillingInput("2026-07")));
        var draft = Assert.Single(run.Bills);

        _clock.Advance(TimeSpan.FromMinutes(1));

        return await _host.WithBillsAsync(register => register.IssueAsync(draft.Id, new IssueBillInput()));
    }

    private Task<Bill> ReloadAsync(Guid billId) =>
        _host.WithBillsAsync(async register => (await register.FindAsync(billId))!);

    private static CustomerDepositApplied Application(Bill bill, decimal amount, DateTimeOffset at) =>
        CustomerDepositApplied.For(
            at,
            depositEntryId: Guid.CreateVersion7(at),
            customerId: bill.CustomerId,
            serviceAccountId: bill.ServiceAccountId,
            billId: bill.Id,
            billNumber: bill.BillNumber,
            amount: amount,
            balanceAfter: 0m,
            currency: bill.Currency);

    /// <summary>Delivers <paramref name="applied"/> the way the bus would — the real handler, dedupe store and consumer name.</summary>
    private Task<bool> ConsumeAsync(CustomerDepositApplied applied) =>
        _host.InScopeAsync(services =>
            services.GetRequiredService<IdempotentEventHandler>().HandleAsync(
                applied.EventId,
                CustomerDepositAppliedConsumer.Name,
                token => BillDeposits.ApplyAsync(services.GetRequiredService<IBillService>(), applied, token)));

    [Fact]
    public async Task An_applied_deposit_reduces_what_the_bill_is_owed()
    {
        var bill = await AnIssuedBillAsync();
        var part = decimal.Round(bill.AmountDue / 2, 2);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await ConsumeAsync(Application(bill, part, _clock.GetUtcNow()));

        var settled = await ReloadAsync(bill.Id);

        Assert.Equal(part, settled.AmountPaid);
        Assert.Equal(bill.AmountDue - part, settled.Balance);
        Assert.Equal(BillStatus.PartiallyPaid, settled.Status);

        // The printed total never moves. It is what the customer holds a copy of.
        Assert.Equal(bill.TotalAmount, settled.TotalAmount);
    }

    [Fact]
    public async Task A_deposit_that_covers_the_balance_settles_the_bill()
    {
        var bill = await AnIssuedBillAsync();

        _clock.Advance(TimeSpan.FromMinutes(1));
        await ConsumeAsync(Application(bill, bill.Balance, _clock.GetUtcNow()));

        var settled = await ReloadAsync(bill.Id);

        Assert.Equal(BillStatus.Paid, settled.Status);
        Assert.Equal(0m, settled.Balance);
        Assert.NotNull(settled.PaidAt);
    }

    [Fact]
    public async Task An_applied_deposit_leaves_the_bills_own_adjustment_trail_alone()
    {
        // WORK_PACKAGES.md's explicit requirement for this package, and WP-2.4's rule restated:
        // money moving is not a lifecycle state. A deposit recorded as a credit adjustment would
        // change what the bill says it charged, which is a different and untrue claim.
        var bill = await AnIssuedBillAsync();

        _clock.Advance(TimeSpan.FromMinutes(1));
        await ConsumeAsync(Application(bill, bill.Balance, _clock.GetUtcNow()));

        var settled = await ReloadAsync(bill.Id);

        Assert.Empty(settled.Adjustments);
        Assert.Equal(0m, settled.AdjustmentTotal);
        Assert.Equal(bill.AmountDue, settled.AmountDue);
    }

    [Fact]
    public async Task A_redelivered_application_moves_the_balance_exactly_once()
    {
        // A broker redelivers. Without the claim the idempotent consumer takes, the same deposit
        // would settle the bill twice and a customer would be shown as having paid money that was
        // only ever held once.
        var bill = await AnIssuedBillAsync();
        var part = decimal.Round(bill.AmountDue / 4, 2);

        _clock.Advance(TimeSpan.FromMinutes(1));

        var applied = Application(bill, part, _clock.GetUtcNow());

        Assert.True(await ConsumeAsync(applied));
        Assert.False(await ConsumeAsync(applied));

        Assert.Equal(part, (await ReloadAsync(bill.Id)).AmountPaid);
    }

    [Fact]
    public async Task An_application_is_audited_as_a_deposit_rather_than_as_a_payment()
    {
        // "Was this bill settled with cash or out of the deposit" is exactly what somebody asks of a
        // closed account, and the action name is what makes it a filter rather than a diff.
        var bill = await AnIssuedBillAsync();

        _clock.Advance(TimeSpan.FromMinutes(1));
        await ConsumeAsync(Application(bill, bill.Balance, _clock.GetUtcNow()));

        await using var platform = _host.NewPlatformContext();

        var actions = await platform.AuditEntries
            .Where(entry => entry.EntityId == bill.Id.ToString())
            .Select(entry => entry.Action)
            .ToListAsync();

        Assert.Contains(AuditActions.BillDepositApplied, actions);
        Assert.DoesNotContain(AuditActions.BillPaymentRecorded, actions);
    }

    [Fact]
    public async Task A_deposit_larger_than_the_balance_is_refused_rather_than_absorbed()
    {
        // Failure path. Customers asks IBillDirectory what is owed before it publishes, so this is
        // the race — a payment landing first — and the answer is deliberately a throw: the message
        // dead-letters, and a bill that quietly swallowed the difference would leave money with no
        // record of where it went.
        var bill = await AnIssuedBillAsync();

        _clock.Advance(TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<BillingWorkflowException>(() =>
            ConsumeAsync(Application(bill, bill.Balance + 0.01m, _clock.GetUtcNow())));

        Assert.Equal(0m, (await ReloadAsync(bill.Id)).AmountPaid);
    }

    [Fact]
    public async Task An_application_publishes_nothing()
    {
        // Finance already heard the fact from Customers — CustomerDepositApplied is what it posts
        // the transfer from — and a second event saying the same money moved is how a ledger gets a
        // duplicate entry.
        var bill = await AnIssuedBillAsync();

        var before = _host.Events.Published.Count;

        _clock.Advance(TimeSpan.FromMinutes(1));
        await ConsumeAsync(Application(bill, bill.Balance, _clock.GetUtcNow()));

        Assert.Equal(before, _host.Events.Published.Count);
    }
}
