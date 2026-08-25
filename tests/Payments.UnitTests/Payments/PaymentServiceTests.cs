using GridCore.Contracts.Events;
using GridCore.Contracts.Providers;
using GridCore.Modules.Payments.Features.Payments;
using GridCore.Modules.Payments.Features.Shared;
using GridCore.Modules.Payments.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Payments.UnitTests.Payments;

/// <summary>
/// The payments register over a real EF model on SQLite in-memory. What these assert that the
/// aggregate tests cannot: the payment row, its audit entry and its <c>PaymentApproved</c> outbox
/// row are one transaction, and a refusal publishes nothing at all.
/// </summary>
public sealed class PaymentServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeClock _clock = new(Now);
    private readonly PaymentsTestHost _host;

    public PaymentServiceTests() =>
        _host = new PaymentsTestHost(_clock, new FakeCurrentUser("clerk-1", "Ana Reyes"));

    public void Dispose() => _host.Dispose();

    /// <summary>
    /// Takes a payment, a minute after the last one.
    /// </summary>
    /// <remarks>
    /// The clock advance is load-bearing, not decoration. A payment's id is a Guid v7 stamped from
    /// the clock, so two taken inside one frozen millisecond have no defined order at all — and
    /// this register is ordered by key. STATUS.md has warned about this since WP-0.5; it is still
    /// the easiest trap in the codebase to fall into, and it is also what really happens: two
    /// payments are not taken at the same instant.
    /// </remarks>
    private Task<PaymentResult> TakeAsync(Guid billId, decimal amount = 50.00m, string method = PaymentMethods.Card)
    {
        _clock.Advance(TimeSpan.FromMinutes(1));

        return _host.WithPaymentsAsync(register =>
            register.TakeAsync(new TakePaymentInput(billId, amount, method, "•••• 4242")));
    }

    [Fact]
    public async Task An_approved_payment_is_recorded_audited_and_published_together()
    {
        var (_, bill) = _host.AnOutstandingBill(amountDue: 120.00m);

        var result = await TakeAsync(bill.Id);

        Assert.True(result.Approved());
        Assert.Equal(PaymentStatus.Approved, result.Payment.Status);

        // The row.
        await using var payments = _host.NewPaymentsContext();
        var stored = await payments.Payments.SingleAsync();

        Assert.Equal("PAY-000001", stored.PaymentNumber);
        Assert.Equal(50.00m, stored.Amount);
        Assert.Equal(bill.Id, stored.BillId);
        Assert.Equal("Stub payment provider", stored.ProviderName);

        // The audit entry (invariant 1), in the same transaction.
        await using var platform = _host.NewPlatformContext();
        var entry = await platform.AuditEntries.SingleAsync();

        Assert.Equal(AuditActions.PaymentTaken, entry.Action);
        Assert.Equal(AuditEntityTypes.Payment, entry.EntityType);
        Assert.Equal(stored.Id.ToString(), entry.EntityId);

        // The event (invariant 2).
        var published = _host.Events.Single<PaymentApproved>();

        Assert.Equal(stored.Id, published.PaymentId);
        Assert.Equal(bill.Id, published.BillId);
        Assert.Equal(50.00m, published.Amount);
        Assert.Equal("USD", published.Currency);
        Assert.Equal(PaymentMethods.Card, published.Method);
        Assert.Equal(stored.ProviderReference, published.ProviderReference);
    }

    [Fact]
    public async Task Exactly_one_event_is_published_for_one_approval()
    {
        // Half of the work package's "exactly one balance change + event". The other half — that a
        // redelivery of that one event moves a balance once — belongs to Billing's consumer and is
        // asserted there.
        var (_, bill) = _host.AnOutstandingBill();

        await TakeAsync(bill.Id);

        Assert.Single(_host.Events.Published);
        Assert.Single(_host.Events.Published.OfType<PaymentApproved>());
    }

    [Theory]
    [InlineData(PaymentOutcome.Declined, PaymentStatus.Declined)]
    [InlineData(PaymentOutcome.InsufficientFunds, PaymentStatus.Declined)]
    [InlineData(PaymentOutcome.Timeout, PaymentStatus.Failed)]
    public async Task A_refusal_is_recorded_and_audited_but_never_published(
        PaymentOutcome outcome,
        PaymentStatus expected)
    {
        // No money moved, so there is no receivable to relieve and no cash to post. An event for a
        // decline would be one every consumer had to learn to ignore.
        var (_, bill) = _host.AnOutstandingBill();

        _host.Provider.WillAnswer(outcome);

        var result = await TakeAsync(bill.Id);

        Assert.False(result.Approved());
        Assert.Equal(expected, result.Payment.Status);
        Assert.Equal(outcome, result.Payment.Outcome);

        await using var payments = _host.NewPaymentsContext();
        Assert.Equal(1, await payments.Payments.CountAsync());

        // Audited all the same: a run of declines on one account is exactly what somebody comes
        // looking for.
        await using var platform = _host.NewPlatformContext();
        Assert.Equal(1, await platform.AuditEntries.CountAsync());

        Assert.Empty(_host.Events.Published);
    }

    [Fact]
    public async Task A_retry_after_a_refusal_is_a_new_payment_and_can_succeed()
    {
        // Which is what the register has to be able to show: the failed attempt stays on the
        // account and the successful one sits beside it.
        var (_, bill) = _host.AnOutstandingBill();

        _host.Provider.WillAnswer(PaymentOutcome.Declined);

        await TakeAsync(bill.Id);
        var second = await TakeAsync(bill.Id);

        Assert.True(second.Approved());

        await using var payments = _host.NewPaymentsContext();
        var stored = await payments.Payments.OrderBy(payment => payment.PaymentNumber).ToListAsync();

        Assert.Equal(["PAY-000001", "PAY-000002"], stored.Select(payment => payment.PaymentNumber));
        Assert.Equal([PaymentStatus.Declined, PaymentStatus.Approved], stored.Select(payment => payment.Status));
    }

    [Fact]
    public async Task Paying_more_than_is_owed_is_refused_without_the_provider_being_asked()
    {
        // THE FAILURE PATH THIS WORK PACKAGE IS ABOUT. The utility must not authorise money the
        // bill cannot accept, so the guard runs before anybody is charged — not after.
        var (_, bill) = _host.AnOutstandingBill(amountDue: 120.00m);

        var thrown = await Assert.ThrowsAsync<PaymentWorkflowException>(() => TakeAsync(bill.Id, amount: 120.01m));

        Assert.Contains("more than is owed", thrown.Message, StringComparison.Ordinal);

        // Nothing was asked of the provider, nothing was written and nothing was published.
        Assert.Empty(_host.Provider.Requests);
        Assert.Empty(_host.Events.Published);

        await using var payments = _host.NewPaymentsContext();
        Assert.Equal(0, await payments.Payments.CountAsync());
    }

    [Fact]
    public async Task A_bill_nobody_owes_is_refused_without_the_provider_being_asked()
    {
        var account = _host.Accounts.Add();
        var paid = _host.Bills.Add(account.Id, account.CustomerId, status: "Paid");

        await Assert.ThrowsAsync<PaymentWorkflowException>(() => TakeAsync(paid.Id));

        Assert.Empty(_host.Provider.Requests);
    }

    [Fact]
    public async Task A_bill_the_register_has_never_heard_of_is_a_404()
    {
        await Assert.ThrowsAsync<BillNotFoundException>(() => TakeAsync(Guid.CreateVersion7()));

        Assert.Empty(_host.Provider.Requests);
    }

    [Fact]
    public async Task A_bill_naming_an_account_customers_does_not_know_is_a_404()
    {
        // The seam answering "no" is a real outcome: the bill's account may have been purged, and
        // taking money onto an account nobody can name is worse than refusing.
        var (account, bill) = _host.AnOutstandingBill();

        _host.Accounts.Forget(account.Id);

        await Assert.ThrowsAsync<ServiceAccountNotFoundException>(() => TakeAsync(bill.Id));
    }

    [Fact]
    public async Task A_payment_finer_than_a_cent_is_refused_without_the_provider_being_asked()
    {
        var (_, bill) = _host.AnOutstandingBill();

        await Assert.ThrowsAsync<PaymentValidationException>(() => TakeAsync(bill.Id, amount: 50.005m));

        Assert.Empty(_host.Provider.Requests);
    }

    [Fact]
    public async Task A_method_the_utility_does_not_accept_is_refused_without_the_provider_being_asked()
    {
        var (_, bill) = _host.AnOutstandingBill();

        await Assert.ThrowsAsync<PaymentValidationException>(() => TakeAsync(bill.Id, method: "cheque"));

        Assert.Empty(_host.Provider.Requests);
    }

    [Fact]
    public async Task A_provider_that_answers_with_no_reference_leaves_nothing_behind()
    {
        // The whole write rolls back — the row, the audit entry and the event alike — because it
        // all lives in one unit of work. A payment with no reference cannot be reconciled, so half
        // of it must not survive.
        var (_, bill) = _host.AnOutstandingBill();

        _host.Provider.Reference = "   ";

        await Assert.ThrowsAsync<PaymentValidationException>(() => TakeAsync(bill.Id));

        await using var payments = _host.NewPaymentsContext();
        await using var platform = _host.NewPlatformContext();

        Assert.Equal(0, await payments.Payments.CountAsync());
        Assert.Equal(0, await platform.AuditEntries.CountAsync());
        Assert.Empty(_host.Events.Published);
    }

    [Fact]
    public async Task The_provider_is_asked_with_the_payments_id_as_its_idempotency_key()
    {
        var (_, bill) = _host.AnOutstandingBill();

        var result = await TakeAsync(bill.Id);
        var asked = Assert.Single(_host.Provider.Requests);

        Assert.Equal(result.Payment.Id, asked.PaymentId);
        Assert.Equal(result.Payment.PaymentNumber, asked.Reference);
    }

    [Fact]
    public async Task The_bill_is_read_through_the_seam_rather_than_from_a_billing_table()
    {
        // The point of IBillDirectory: this module has never heard of a billing schema, and the
        // fast tier proves it by not having one.
        var (_, bill) = _host.AnOutstandingBill();

        await TakeAsync(bill.Id);

        Assert.Contains(bill.Id, _host.Bills.Lookups);
    }

    [Fact]
    public async Task Payment_numbers_are_issued_in_order_and_never_repeat()
    {
        var (account, _) = _host.AnOutstandingBill();

        for (var index = 0; index < 3; index++)
        {
            await TakeAsync(_host.Bills.Add(account.Id, account.CustomerId).Id);
        }

        await using var payments = _host.NewPaymentsContext();
        var numbers = await payments.Payments
            .OrderBy(payment => payment.PaymentNumber)
            .Select(payment => payment.PaymentNumber)
            .ToListAsync();

        Assert.Equal(["PAY-000001", "PAY-000002", "PAY-000003"], numbers);
    }

    [Fact]
    public async Task The_audit_trail_never_carries_the_instrument()
    {
        // It is on the payment because a clerk needs to say which card was used. The audit trail is
        // read far more widely and by people with no business knowing.
        var (_, bill) = _host.AnOutstandingBill();

        await TakeAsync(bill.Id);

        await using var platform = _host.NewPlatformContext();
        var entry = await platform.AuditEntries.SingleAsync();

        Assert.DoesNotContain("4242", entry.AfterJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_register_lists_what_it_is_asked_for_newest_first()
    {
        var (account, first) = _host.AnOutstandingBill();
        var second = _host.Bills.Add(account.Id, account.CustomerId);

        await TakeAsync(first.Id);
        await TakeAsync(second.Id);

        var listed = await _host.WithPaymentsAsync(register => register.ListAsync(new PaymentQuery()));

        Assert.Equal(["PAY-000002", "PAY-000001"], listed.Select(payment => payment.PaymentNumber));

        var forBill = await _host.WithPaymentsAsync(register =>
            register.ListAsync(new PaymentQuery(BillId: first.Id)));

        Assert.Equal(["PAY-000001"], forBill.Select(payment => payment.PaymentNumber));
    }

    [Fact]
    public async Task The_takings_list_holds_only_money_the_utility_actually_has()
    {
        var (account, approved) = _host.AnOutstandingBill();
        var refused = _host.Bills.Add(account.Id, account.CustomerId);

        await TakeAsync(approved.Id);

        _host.Provider.WillAnswer(PaymentOutcome.Declined);
        await TakeAsync(refused.Id);

        var settled = await _host.WithPaymentsAsync(register =>
            register.ListAsync(new PaymentQuery(SettledOnly: true)));

        Assert.All(settled, payment => Assert.True(payment.IsSettled));
        Assert.Single(settled);
    }

    [Fact]
    public async Task One_payment_is_read_back_by_its_id()
    {
        var (_, bill) = _host.AnOutstandingBill();
        var taken = await TakeAsync(bill.Id);

        Assert.Equal(taken.Payment.Id, (await _host.WithPaymentsAsync(register => register.FindAsync(taken.Payment.Id)))!.Id);
        Assert.Null(await _host.WithPaymentsAsync(register => register.FindAsync(Guid.CreateVersion7())));
    }
}

/// <summary>Small readability helper: a payment result's headline answer.</summary>
internal static class PaymentResultAssertions
{
    /// <summary>Whether the money moved.</summary>
    public static bool Approved(this PaymentResult result) => result.Payment.IsSettled;
}
