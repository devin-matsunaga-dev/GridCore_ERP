using GridCore.Contracts.Events;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Modules.Billing.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Billing.UnitTests.Bills;

/// <summary>
/// The other half of WP-2.5: what an approved payment does to a bill. Payments states that money
/// arrived; this module decides what that means for the document, and this is where the work
/// package's "exactly one balance change, idempotent on retry" is actually proved.
/// </summary>
public sealed class BillPaymentTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeClock _clock = new(Now);
    private readonly BillingTestHost _host;

    /// <remarks>
    /// No explicit current user, unlike the other billing suites. A consumer runs outside any
    /// request, so <see cref="ICurrentUser"/> really does resolve to <see cref="SystemUser"/> — and
    /// the audit entry a payment leaves on a bill has to be attributed that way. Configuring a clerk
    /// here would prove the opposite of what happens in the host.
    /// </remarks>
    public BillPaymentTests() => _host = new BillingTestHost(_clock);

    public void Dispose() => _host.Dispose();

    /// <summary>Bills one seeded reading and issues it, which is the state a payment arrives at.</summary>
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

    private static PaymentApproved Approval(Bill bill, decimal amount, DateTimeOffset at) =>
        PaymentApproved.For(
            at,
            Guid.CreateVersion7(at),
            bill.ServiceAccountId,
            bill.CustomerId,
            bill.Id,
            amount,
            bill.Currency,
            "card",
            "SIM-PAY-000001");

    /// <summary>
    /// Delivers <paramref name="approved"/> the way the bus would.
    /// </summary>
    /// <remarks>
    /// The real <see cref="IdempotentEventHandler"/>, the real dedupe store, the real unit of work
    /// and the real consumer name — everything <see cref="PaymentApprovedConsumer"/> does except
    /// the four lines of MassTransit glue that unwrap a <c>ConsumeContext</c>, which is the
    /// platform's to test and is why the handler was built free of MassTransit types in the first
    /// place.
    /// </remarks>
    private Task<bool> ConsumeAsync(PaymentApproved approved) =>
        _host.InScopeAsync(services =>
            services.GetRequiredService<IdempotentEventHandler>().HandleAsync(
                approved.EventId,
                PaymentApprovedConsumer.Name,
                token => BillPayments.ApplyAsync(services.GetRequiredService<IBillService>(), approved, token)));

    [Fact]
    public async Task An_approved_payment_reduces_what_the_bill_is_owed()
    {
        var bill = await AnIssuedBillAsync();
        var part = decimal.Round(bill.AmountDue / 2, 2);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await ConsumeAsync(Approval(bill, part, _clock.GetUtcNow()));

        var settled = await ReloadAsync(bill.Id);

        Assert.Equal(part, settled.AmountPaid);
        Assert.Equal(bill.AmountDue - part, settled.Balance);
        Assert.Equal(BillStatus.PartiallyPaid, settled.Status);

        // The printed total never moves. It is what the customer holds a copy of.
        Assert.Equal(bill.TotalAmount, settled.TotalAmount);
    }

    [Fact]
    public async Task Paying_the_balance_in_full_settles_the_bill()
    {
        var bill = await AnIssuedBillAsync();

        _clock.Advance(TimeSpan.FromMinutes(1));
        await ConsumeAsync(Approval(bill, bill.Balance, _clock.GetUtcNow()));

        var settled = await ReloadAsync(bill.Id);

        Assert.Equal(BillStatus.Paid, settled.Status);
        Assert.Equal(0m, settled.Balance);
        Assert.NotNull(settled.PaidAt);
    }

    [Fact]
    public async Task A_redelivered_approval_moves_the_balance_exactly_once()
    {
        // THE WORK PACKAGE'S HEADLINE REQUIREMENT. A broker redelivers; without the claim the
        // idempotent consumer takes, the same approval would reduce the balance twice and a
        // customer would be shown as having paid money they never sent.
        var bill = await AnIssuedBillAsync();
        var part = decimal.Round(bill.AmountDue / 4, 2);

        _clock.Advance(TimeSpan.FromMinutes(1));

        var approved = Approval(bill, part, _clock.GetUtcNow());

        await ConsumeAsync(approved);
        await ConsumeAsync(approved);
        await ConsumeAsync(approved);

        var settled = await ReloadAsync(bill.Id);

        Assert.Equal(part, settled.AmountPaid);
        Assert.Equal(BillStatus.PartiallyPaid, settled.Status);
    }

    [Fact]
    public async Task Two_different_payments_both_land()
    {
        // The flip side of idempotency: dedupe is per event, not per bill. Two instalments are two
        // facts and both reduce the balance.
        var bill = await AnIssuedBillAsync();
        var part = decimal.Round(bill.AmountDue / 4, 2);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await ConsumeAsync(Approval(bill, part, _clock.GetUtcNow()));

        _clock.Advance(TimeSpan.FromMinutes(1));
        await ConsumeAsync(Approval(bill, part, _clock.GetUtcNow()));

        Assert.Equal(part * 2, (await ReloadAsync(bill.Id)).AmountPaid);
    }

    [Fact]
    public async Task Applying_a_payment_leaves_an_audit_entry_showing_the_money_move()
    {
        // Invariant 1 from a consumer rather than an endpoint. Recorded against the system user,
        // which is correct: nobody at a keyboard reduced this balance, an approved payment did —
        // and the clerk who took the money is named on the payment's own entry, one module over.
        var bill = await AnIssuedBillAsync();

        _clock.Advance(TimeSpan.FromMinutes(1));
        await ConsumeAsync(Approval(bill, bill.Balance, _clock.GetUtcNow()));

        await using var platform = _host.NewPlatformContext();

        var entry = await platform.AuditEntries
            .Where(entry => entry.Action == AuditActions.BillPaymentRecorded)
            .SingleAsync();

        Assert.Equal(AuditEntityTypes.Bill, entry.EntityType);
        Assert.Equal(bill.Id.ToString(), entry.EntityId);
        Assert.Equal(SystemUser.SystemUserId, entry.UserId);
        Assert.NotNull(entry.BeforeJson);
        Assert.NotNull(entry.AfterJson);
    }

    [Fact]
    public async Task Applying_a_payment_publishes_nothing()
    {
        // Finance already heard the fact from Payments — PaymentApproved is what it posts the cash
        // receipt from — and a second event saying the same money arrived is how a ledger gets a
        // duplicate entry.
        var bill = await AnIssuedBillAsync();

        _host.Events.Published.Clear();

        _clock.Advance(TimeSpan.FromMinutes(1));
        await ConsumeAsync(Approval(bill, bill.Balance, _clock.GetUtcNow()));

        Assert.Empty(_host.Events.Published);
    }

    [Fact]
    public async Task A_payment_is_measured_against_the_balance_a_credit_left_behind()
    {
        // WP-2.4's split, reaching WP-2.5. A bill credited to half its printed total is owed half,
        // and a payment for the printed total would be an overpayment the aggregate refuses.
        var bill = await AnIssuedBillAsync();
        var credit = decimal.Round(bill.TotalAmount / 2, 2);

        _clock.Advance(TimeSpan.FromMinutes(1));

        var corrected = await _host.WithBillsAsync(register => register.AdjustAsync(
            bill.Id,
            new AdjustBillInput(BillAdjustmentKind.Credit, credit, "Estimated read corrected.")));

        Assert.Equal(bill.TotalAmount - credit, corrected.AmountDue);

        // Paying what the bill SAYS is now more than is owed.
        _clock.Advance(TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<BillingWorkflowException>(() =>
            ConsumeAsync(Approval(bill, bill.TotalAmount, _clock.GetUtcNow())));

        // Paying what is owed settles it.
        _clock.Advance(TimeSpan.FromMinutes(1));
        await ConsumeAsync(Approval(bill, corrected.AmountDue, _clock.GetUtcNow()));

        var settled = await ReloadAsync(bill.Id);

        Assert.Equal(BillStatus.Paid, settled.Status);
        Assert.Equal(corrected.AmountDue, settled.AmountPaid);
    }

    [Fact]
    public async Task An_overpayment_leaves_the_bill_untouched()
    {
        // The consumer's failure path. The balance pre-check lives in Payments and refuses before
        // anybody is charged; this is the backstop for the race where the balance moved in between,
        // and it must leave the bill exactly as it was rather than let it go negative.
        var bill = await AnIssuedBillAsync();

        _clock.Advance(TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<BillingWorkflowException>(() =>
            ConsumeAsync(Approval(bill, bill.Balance + 0.01m, _clock.GetUtcNow())));

        var untouched = await ReloadAsync(bill.Id);

        Assert.Equal(0m, untouched.AmountPaid);
        Assert.Equal(BillStatus.Issued, untouched.Status);
    }

    [Fact]
    public async Task An_approval_naming_a_bill_this_module_does_not_know_fails_rather_than_passing_quietly()
    {
        // A 404 from a consumer faults the message and it is retried and then dead-lettered, which
        // is right: money arrived and no document could be found to apply it to, and that is
        // somebody's morning, not a row to drop.
        var bill = await AnIssuedBillAsync();

        var stray = Approval(bill, 10.00m, Now) with { BillId = Guid.CreateVersion7(Now) };

        await Assert.ThrowsAsync<BillNotFoundException>(() => ConsumeAsync(stray));
    }

    [Fact]
    public async Task An_approval_against_no_bill_at_all_is_ignored()
    {
        // The event's BillId is nullable because a later work package will take account-level
        // payments. Ignored rather than faulted: a consumer that threw on a fact it has no work for
        // would park the message on a dead-letter queue for no reason.
        var bill = await AnIssuedBillAsync();

        await ConsumeAsync(Approval(bill, 10.00m, Now) with { BillId = null });

        Assert.Equal(0m, (await ReloadAsync(bill.Id)).AmountPaid);
    }

    [Fact]
    public void The_consumers_dedupe_name_is_its_own_and_not_the_ledgers() =>
        // Both Billing and Finance consume PaymentApproved and each has its own work to do with it.
        // A shared name would mean whichever module handled the event first silently suppressed the
        // other — a bill that was never reduced, or a receipt that was never posted.
        Assert.Equal("billing.payment-approved", PaymentApprovedConsumer.Name);
}
