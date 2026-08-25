using GridCore.Contracts.Providers;
using GridCore.Modules.Payments.Features.Payments;
using GridCore.Modules.Payments.Simulation;
using GridCore.Modules.Payments.UnitTests.Infrastructure;

namespace GridCore.Modules.Payments.UnitTests.Simulation;

/// <summary>
/// The payment sandbox. What matters about it is not that it refuses a plausible proportion but
/// that it refuses the <b>same</b> ones every time: a demonstration whose outcomes move between
/// runs cannot be rehearsed, and a test that cannot predict a decline cannot assert one.
/// </summary>
public sealed class SimulatedPaymentProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    private static SimulatedPaymentProvider Provider() => new(new FakeClock(Now));

    private static PaymentAuthorizationRequest Request(
        int ordinal = 1,
        string method = PaymentMethods.Card,
        string? instrument = "•••• 4242") =>
        new(Guid.CreateVersion7(), $"PAY-{ordinal:D6}", 50.00m, "USD", method, instrument);

    private static async Task<PaymentOutcome> OutcomeAsync(PaymentAuthorizationRequest request) =>
        (await Provider().AuthorizeAsync(request)).Outcome;

    private static async Task<IReadOnlyList<PaymentOutcome>> SweepAsync(int count, string instrument = "•••• 4242")
    {
        var provider = Provider();
        var outcomes = new List<PaymentOutcome>();

        for (var ordinal = 1; ordinal <= count; ordinal++)
        {
            outcomes.Add((await provider.AuthorizeAsync(Request(ordinal, instrument: instrument))).Outcome);
        }

        return outcomes;
    }

    [Fact]
    public async Task The_same_payment_number_always_gets_the_same_answer()
    {
        // The work package's headline requirement, and the reason the stream is keyed on the number
        // rather than on the id: two freshly seeded databases hold the same payments under
        // different Guids, and a demonstration whose declines moved per machine could not be
        // rehearsed.
        var first = await OutcomeAsync(Request(ordinal: 42));
        var second = await OutcomeAsync(Request(ordinal: 42));

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task A_payments_answer_does_not_depend_on_the_id_it_happens_to_carry()
    {
        // Two requests with the same number and different ids. If this failed, every seeded demo
        // world would decline a different customer.
        var request = Request(ordinal: 42);

        Assert.Equal(
            await OutcomeAsync(request),
            await OutcomeAsync(request with { PaymentId = Guid.CreateVersion7() }));
    }

    [Fact]
    public async Task A_different_payment_number_can_get_a_different_answer() =>
        // Which is what makes a retry meaningful: a refused payment is retried as a new attempt
        // with a new number, so it draws a new stream and may well be approved.
        Assert.True((await SweepAsync(400)).Distinct().Count() > 1);

    [Fact]
    public async Task Every_outcome_a_charge_can_produce_appears_over_a_realistic_run()
    {
        // WORK_PACKAGES.md asks for Approved, Declined, InsufficientFunds and Timeout. Asserted
        // over 400 attempts, where each is near-certain rather than merely likely.
        var outcomes = await SweepAsync(400);

        Assert.Contains(PaymentOutcome.Approved, outcomes);
        Assert.Contains(PaymentOutcome.Declined, outcomes);
        Assert.Contains(PaymentOutcome.InsufficientFunds, outcomes);
        Assert.Contains(PaymentOutcome.Timeout, outcomes);
    }

    [Fact]
    public async Task A_charge_is_never_answered_with_a_refund()
    {
        // The fifth outcome is declared on the seam because it is one the seam has to carry, but a
        // refund is a different act — nothing in GridCore performs one yet, and a charge that came
        // back Refunded would be an approval nobody could account for.
        var outcomes = await SweepAsync(400);

        Assert.DoesNotContain(PaymentOutcome.Refunded, outcomes);
    }

    [Fact]
    public async Task Most_payments_are_approved()
    {
        // A sandbox that refused half its attempts would make the demonstration about failure. The
        // advertised chances add up to under a tenth; loose bounds, because this is a sample of 400
        // rather than a proof about the distribution.
        var outcomes = await SweepAsync(400);

        Assert.InRange(outcomes.Count(outcome => outcome is PaymentOutcome.Approved), 320, 400);
    }

    [Fact]
    public async Task The_refusal_rates_are_roughly_what_the_sandbox_advertises()
    {
        // A drifting constant would quietly change every demonstration, so the advertised chances
        // are held to what actually comes out.
        var outcomes = await SweepAsync(400);

        Assert.InRange(outcomes.Count(outcome => outcome is PaymentOutcome.Declined), 1, 50);
        Assert.InRange(outcomes.Count(outcome => outcome is PaymentOutcome.InsufficientFunds), 1, 45);
        Assert.InRange(outcomes.Count(outcome => outcome is PaymentOutcome.Timeout), 1, 40);
    }

    [Theory]
    [InlineData(SimulatedPaymentProvider.DeclinedInstrumentSuffix, PaymentOutcome.Declined)]
    [InlineData(SimulatedPaymentProvider.InsufficientFundsInstrumentSuffix, PaymentOutcome.InsufficientFunds)]
    [InlineData(SimulatedPaymentProvider.TimeoutInstrumentSuffix, PaymentOutcome.Timeout)]
    public async Task A_pinned_instrument_always_produces_its_refusal(string suffix, PaymentOutcome expected)
    {
        // The way every real sandbox works, and what makes the failure path demonstrable: a
        // demonstration shows a decline on purpose instead of taking payments until one happens.
        // Pinned across many payment numbers, so it beats the stream rather than agreeing with it
        // by luck.
        foreach (var ordinal in new[] { 1, 7, 33, 128, 999 })
        {
            Assert.Equal(expected, await OutcomeAsync(Request(ordinal, instrument: $"•••• {suffix}")));
        }
    }

    [Fact]
    public async Task Cash_is_never_refused()
    {
        // The money is already in the drawer. A demonstration in which the till refuses a customer
        // standing at the counter reads as a bug.
        var provider = Provider();
        var cash = new List<PaymentOutcome>();
        var card = new List<PaymentOutcome>();

        for (var ordinal = 1; ordinal <= 200; ordinal++)
        {
            cash.Add((await provider.AuthorizeAsync(Request(ordinal, PaymentMethods.Cash, instrument: null))).Outcome);
            card.Add((await provider.AuthorizeAsync(Request(ordinal))).Outcome);
        }

        Assert.All(cash, outcome => Assert.Equal(PaymentOutcome.Approved, outcome));

        // The same payment numbers put through as cards DID draw refusals, so the assertion above
        // is passing because cash is exempt rather than because the sample happened to be clean.
        Assert.Contains(card, outcome => outcome is not PaymentOutcome.Approved);
    }

    [Fact]
    public async Task Cash_ignores_a_pinned_instrument_too() =>
        // Notes and coins do not have a card number to be refused for.
        Assert.Equal(
            PaymentOutcome.Approved,
            await OutcomeAsync(Request(1, PaymentMethods.Cash, $"•••• {SimulatedPaymentProvider.DeclinedInstrumentSuffix}")));

    [Fact]
    public async Task Every_answer_carries_a_reference_to_reconcile_against()
    {
        // Refusals included: "which attempt was this" is asked of failures more often than of
        // successes. Derived from the payment number so a demonstration's references are the same
        // on every machine, and prefixed so nobody mistakes one for a real acquirer's.
        var provider = Provider();

        foreach (var ordinal in new[] { 1, 2, 3, 4, 5 })
        {
            var answer = await provider.AuthorizeAsync(Request(ordinal));

            Assert.Equal($"SIM-PAY-{ordinal:D6}", answer.ProviderReference);
        }
    }

    [Fact]
    public async Task A_refusal_says_something_a_clerk_can_repeat_and_an_approval_says_nothing()
    {
        var declined = await Provider().AuthorizeAsync(
            Request(instrument: $"•••• {SimulatedPaymentProvider.DeclinedInstrumentSuffix}"));

        var approved = await Provider().AuthorizeAsync(Request(method: PaymentMethods.Cash, instrument: null));

        Assert.False(string.IsNullOrWhiteSpace(declined.Message));
        Assert.Null(approved.Message);
    }

    [Fact]
    public async Task The_answer_is_stamped_with_when_the_provider_decided() =>
        Assert.Equal(Now, (await Provider().AuthorizeAsync(Request())).ProcessedAt);

    [Fact]
    public void The_sandbox_names_itself_for_the_record_it_leaves_on_every_payment() =>
        // A record of where money came from outlives whichever implementation was configured.
        Assert.False(string.IsNullOrWhiteSpace(Provider().Name));
}
