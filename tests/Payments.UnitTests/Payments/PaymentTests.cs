using GridCore.Contracts.Directories;
using GridCore.Contracts.Providers;
using GridCore.Modules.Payments.Features.Payments;
using GridCore.Modules.Payments.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Payments.UnitTests.Payments;

/// <summary>
/// The payment aggregate: what may be taken, what the provider's answer means, and what the utility
/// therefore holds. Pure — no database, no provider, no bus (CONVENTIONS.md ⚡ tier 1).
/// </summary>
public sealed class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly RegistryActor Clerk = new("clerk-1", "Ana Reyes");

    private static ServiceAccountSummary Account(Guid? id = null, Guid? customerId = null) =>
        new(
            id ?? Guid.CreateVersion7(Now),
            "A-000001",
            customerId ?? Guid.CreateVersion7(Now),
            "Elena Sablan",
            Guid.CreateVersion7(Now),
            "Active",
            HoldsPremise: true,
            DateTimeOffset.UnixEpoch);

    private static BillSummary Bill(
        Guid serviceAccountId,
        decimal amountDue = 120.00m,
        decimal amountPaid = 0m,
        string status = "Issued") =>
        new(
            Guid.CreateVersion7(Now),
            "BIL-000001",
            serviceAccountId,
            "A-000001",
            Guid.CreateVersion7(Now),
            "Elena Sablan",
            "USD",
            TotalAmount: 150.00m,
            AmountDue: amountDue,
            AmountPaid: amountPaid,
            Balance: amountDue - amountPaid,
            status,
            IsOutstanding: status is "Issued" or "PartiallyPaid" or "Overdue",
            DueDate: new DateOnly(2026, 9, 10));

    private static Payment Take(
        decimal amount = 50.00m,
        string method = PaymentMethods.Card,
        string? instrument = "•••• 4242",
        decimal amountDue = 120.00m,
        decimal amountPaid = 0m,
        string status = "Issued")
    {
        var account = Account();

        return Payment.Take(
            "PAY-000001",
            account,
            Bill(account.Id, amountDue, amountPaid, status),
            amount,
            method,
            instrument,
            Clerk,
            Now);
    }

    private static PaymentAuthorizationResult Answer(
        PaymentOutcome outcome,
        string reference = "SIM-PAY-000001",
        string? message = null) =>
        new(outcome, reference, Now.AddSeconds(2), message);

    [Fact]
    public void A_payment_starts_pending_and_stamps_what_a_receipt_needs()
    {
        var payment = Take();

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.Outcome);
        Assert.False(payment.IsSettled);

        // Everything a receipt has to be able to show without a second lookup, for the reason a
        // bill stamps the same facts: each belongs to a module free to change it.
        Assert.Equal("PAY-000001", payment.PaymentNumber);
        Assert.Equal("A-000001", payment.AccountNumber);
        Assert.Equal("BIL-000001", payment.BillNumber);
        Assert.Equal("USD", payment.Currency);
        Assert.Equal("clerk-1", payment.ActorId);
        Assert.Equal("Ana Reyes", payment.ActorName);
    }

    [Fact]
    public void What_was_owed_when_the_money_was_asked_for_is_stamped_on_the_payment() =>
        // For reconciliation. A payment that only recorded its own amount could not answer "was
        // this the whole bill or part of it" without re-deriving a balance that has since moved.
        Assert.Equal(120.00m, Take(amount: 50.00m).BalanceBefore);

    [Fact]
    public void The_provider_is_asked_with_the_payments_own_id_as_the_idempotency_key()
    {
        // Which is why the row is minted before the provider is called. A real gateway asked twice
        // for the same key charges once.
        var payment = Take();
        var request = payment.ToAuthorization();

        Assert.Equal(payment.Id, request.PaymentId);
        Assert.Equal(payment.PaymentNumber, request.Reference);
        Assert.Equal(payment.Amount, request.Amount);
        Assert.Equal(payment.Currency, request.Currency);
    }

    [Theory]
    [InlineData(PaymentOutcome.Approved, PaymentStatus.Approved)]
    [InlineData(PaymentOutcome.Declined, PaymentStatus.Declined)]
    [InlineData(PaymentOutcome.InsufficientFunds, PaymentStatus.Declined)]
    [InlineData(PaymentOutcome.Timeout, PaymentStatus.Failed)]
    public void Every_outcome_the_provider_can_answer_a_charge_with_maps_to_a_status(
        PaymentOutcome outcome,
        PaymentStatus expected)
    {
        // The work package's "verify each outcome", at the level that decides what happens to money.
        var payment = Take();

        payment.Settle(Answer(outcome), "Sandbox", Now);

        Assert.Equal(expected, payment.Status);
        Assert.Equal(outcome, payment.Outcome);
    }

    [Fact]
    public void A_shortfall_and_a_refusal_are_the_same_status_but_never_the_same_answer()
    {
        // The status says what the utility holds; the outcome says what the provider said. A clerk
        // on the phone needs the second to explain the first, which is why both are stored.
        var declined = Take();
        var short_ = Take();

        declined.Settle(Answer(PaymentOutcome.Declined), "Sandbox", Now);
        short_.Settle(Answer(PaymentOutcome.InsufficientFunds), "Sandbox", Now);

        Assert.Equal(declined.Status, short_.Status);
        Assert.NotEqual(declined.Outcome, short_.Outcome);
    }

    [Fact]
    public void A_timeout_is_not_a_decline()
    {
        // The distinction the whole enum exists for: the money may have moved and the answer been
        // lost, so it must never be folded in with the refusals and retried blindly.
        var payment = Take();

        payment.Settle(Answer(PaymentOutcome.Timeout), "Sandbox", Now);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.NotEqual(PaymentStatus.Declined, payment.Status);
        Assert.False(payment.IsSettled);
    }

    [Fact]
    public void Only_an_approval_is_money_the_utility_holds()
    {
        var approved = Take();

        approved.Settle(Answer(PaymentOutcome.Approved), "Sandbox", Now);

        Assert.True(approved.IsSettled);
        Assert.Equal(Now.AddSeconds(2), approved.SettledAt);
    }

    [Fact]
    public void What_answered_and_its_reference_are_recorded_on_every_outcome()
    {
        // Refusals included: "which attempt was this" is asked of failures more often than of
        // successes, and a payment with no provider reference cannot be reconciled at all.
        var payment = Take();

        payment.Settle(Answer(PaymentOutcome.Declined, message: "Refused by the issuing bank."), "Sandbox gateway", Now);

        Assert.Equal("Sandbox gateway", payment.ProviderName);
        Assert.Equal("SIM-PAY-000001", payment.ProviderReference);
        Assert.Equal("Refused by the issuing bank.", payment.ProviderMessage);
    }

    [Fact]
    public void A_payment_answered_twice_is_refused_rather_than_overwritten()
    {
        // THE FAILURE PATH that protects the money: the first answer is the one the money followed,
        // and an approved payment quietly re-stamped as declined is money with no record.
        var payment = Take();

        payment.Settle(Answer(PaymentOutcome.Approved), "Sandbox", Now);

        var thrown = Assert.Throws<PaymentWorkflowException>(() =>
            payment.Settle(Answer(PaymentOutcome.Declined), "Sandbox", Now));

        Assert.Contains("already Approved", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(PaymentStatus.Approved, payment.Status);
    }

    [Fact]
    public void A_provider_that_answers_with_no_reference_is_refused()
    {
        var payment = Take();

        Assert.Throws<PaymentValidationException>(() =>
            payment.Settle(Answer(PaymentOutcome.Approved, reference: "   "), "Sandbox", Now));

        // And the payment did not move: the guard runs before anything is written, WP-1.4's
        // ordering rule.
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public void An_outcome_this_utility_does_not_know_is_refused_rather_than_guessed()
    {
        // Cast in from a provider GridCore does not fully know. Guessing whether money moved is the
        // one thing worse than failing — the call BillAdjustment.Signed makes about an unknown kind.
        var payment = Take();

        Assert.Throws<PaymentValidationException>(() =>
            payment.Settle(Answer((PaymentOutcome)99), "Sandbox", Now));

        // And it did not move: not approved by accident, not declined by accident.
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public void A_payment_larger_than_the_balance_is_refused_before_anybody_is_charged()
    {
        // THE MONEY GUARD THIS WORK PACKAGE IS ABOUT. Refused rather than absorbed: crediting past
        // zero leaves money on the account, and a credit balance is Finance's to hold (WP-2.6).
        var thrown = Assert.Throws<PaymentWorkflowException>(() => Take(amount: 120.01m, amountDue: 120.00m));

        Assert.Contains("more than is owed", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_balance_is_what_is_checked_and_not_the_printed_total()
    {
        // WP-2.4's split, and the trap it set: TotalAmount is 150 on this bill while only 120 is
        // owed. A payment of 130 checked against the printed total would be allowed through and
        // then refused by Bill.RecordPayment on the other side of the event.
        Assert.Throws<PaymentWorkflowException>(() => Take(amount: 130.00m, amountDue: 120.00m));

        // And a part-paid bill is measured on what is left, not on what it started at.
        Assert.Throws<PaymentWorkflowException>(() => Take(amount: 50.00m, amountDue: 120.00m, amountPaid: 90.00m));
    }

    [Fact]
    public void Paying_the_balance_exactly_is_allowed() =>
        Assert.Equal(120.00m, Take(amount: 120.00m, amountDue: 120.00m).Amount);

    [Theory]
    [InlineData("Draft")]
    [InlineData("Paid")]
    [InlineData("Cancelled")]
    public void A_bill_nobody_owes_cannot_be_paid(string status)
    {
        var thrown = Assert.Throws<PaymentWorkflowException>(() => Take(status: status));

        Assert.Contains("is not owed", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-25)]
    public void A_payment_must_be_positive(decimal amount) =>
        Assert.Throws<PaymentValidationException>(() => Take(amount: amount));

    [Fact]
    public void A_payment_finer_than_a_cent_is_refused_rather_than_rounded() =>
        // A figure somebody at a counter typed, not one GridCore computed — the rule Money is
        // explicit about, and the same call Bill.RecordPayment makes about the very same number.
        Assert.Throws<PaymentValidationException>(() => Take(amount: 50.005m));

    [Fact]
    public void A_method_the_utility_does_not_accept_is_refused()
    {
        var thrown = Assert.Throws<PaymentValidationException>(() => Take(method: "cheque"));

        // And the message names what is accepted, so the caller can fix it without reading source.
        Assert.Contains(PaymentMethods.Card, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bill_belonging_to_another_account_cannot_be_paid_on_this_one()
    {
        // No state would make this legal, so it is a validation failure rather than a conflict.
        var account = Account();

        Assert.Throws<PaymentValidationException>(() => Payment.Take(
            "PAY-000001",
            account,
            Bill(Guid.CreateVersion7(Now)),
            50.00m,
            PaymentMethods.Card,
            null,
            Clerk,
            Now));
    }

    [Fact]
    public void A_payment_must_be_given_a_number() =>
        Assert.Throws<PaymentValidationException>(() => Payment.Take(
            "  ",
            Account(),
            Bill(Guid.CreateVersion7(Now)),
            50.00m,
            PaymentMethods.Card,
            null,
            Clerk,
            Now));

    [Fact]
    public void Cash_carries_no_instrument_however_much_the_caller_types() =>
        // A label typed for notes and coins is noise on a receipt.
        Assert.Null(Take(method: PaymentMethods.Cash, instrument: "•••• 4242").Instrument);

    [Fact]
    public void A_cards_instrument_is_kept_as_the_utility_is_allowed_to_hold_it() =>
        Assert.Equal("•••• 4242", Take(instrument: "•••• 4242").Instrument);
}

/// <summary>
/// The payment state machine. Its own tests because a UI renders buttons from it and because the
/// moves it forbids are the ones that would lose money.
/// </summary>
public sealed class PaymentTransitionsTests
{
    [Theory]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Approved)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Declined)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Approved, PaymentStatus.Refunded)]
    public void The_moves_a_payment_may_make(PaymentStatus from, PaymentStatus to) =>
        Assert.True(PaymentTransitions.IsAllowed(from, to));

    [Theory]
    [InlineData(PaymentStatus.Declined, PaymentStatus.Approved)]
    [InlineData(PaymentStatus.Failed, PaymentStatus.Approved)]
    [InlineData(PaymentStatus.Approved, PaymentStatus.Declined)]
    [InlineData(PaymentStatus.Refunded, PaymentStatus.Approved)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Refunded)]
    public void A_refusal_is_never_revived_and_an_approval_is_never_unsaid(PaymentStatus from, PaymentStatus to) =>
        // A customer who was declined tries again, which is a NEW attempt with its own instrument,
        // its own provider reference and its own row. Reviving this one would leave the register
        // unable to say how many times the card was refused.
        Assert.False(PaymentTransitions.IsAllowed(from, to));

    [Theory]
    [InlineData(PaymentStatus.Declined)]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Refunded)]
    public void The_terminal_states(PaymentStatus status) => Assert.True(PaymentTransitions.IsFinal(status));

    [Fact]
    public void Only_an_approval_counts_as_money_held()
    {
        Assert.True(PaymentTransitions.IsSettled(PaymentStatus.Approved));

        // Refunded is not: it arrived and went back, so a day's takings that counted it would
        // overstate them by twice the refund.
        Assert.All(
            new[] { PaymentStatus.Pending, PaymentStatus.Declined, PaymentStatus.Failed, PaymentStatus.Refunded },
            status => Assert.False(PaymentTransitions.IsSettled(status)));
    }

    [Fact]
    public void Every_status_is_in_the_machine() =>
        // A status added without a row here would silently be final, which for a payment means
        // money that can never be refunded.
        Assert.All(
            Enum.GetValues<PaymentStatus>(),
            status => Assert.NotNull(PaymentTransitions.AllowedFrom(status)));
}
