using GridCore.Contracts.Events;
using GridCore.Modules.Customers.Features.Arrangements;

namespace GridCore.Modules.Customers.UnitTests.Arrangements;

/// <summary>
/// What an approved payment means to an arrangement (WP-2.20) — the consume path, without a bus or a
/// broker.
/// </summary>
/// <remarks>
/// The split <see cref="ArrangementPayments"/> exists for: the transport, the transaction and the
/// deduplication belong to <c>IdempotentConsumer</c> and are the platform's to test, and what a
/// payment <i>means</i> is this module's. So the meaning is a fast test and the broker is a gate one.
/// </remarks>
public sealed class ArrangementPaymentsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Records what it was asked to settle instead of touching a register.</summary>
    private sealed class RecordingArrangements : IPaymentArrangementService
    {
        public List<(Guid Account, decimal Amount, Guid Payment, string Reference)> Recorded { get; } = [];

        public Task<ArrangementSettlement?> RecordPaymentAsync(
            Guid serviceAccountId,
            decimal amount,
            Guid paymentId,
            string providerReference,
            CancellationToken cancellationToken = default)
        {
            Recorded.Add((serviceAccountId, amount, paymentId, providerReference));

            return Task.FromResult<ArrangementSettlement?>(null);
        }

        public Task<PaymentArrangement> ProposeAsync(Guid serviceAccountId, ProposeArrangementInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentArrangement> ActivateAsync(Guid arrangementId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PaymentArrangement>> ListForAccountAsync(Guid serviceAccountId, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ArrangementLimit>> LimitsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ArrangementReviewResult> ReviewAsync(ReviewArrangementsInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static PaymentApproved AnApproval(Guid serviceAccountId, decimal amount, Guid? billId = null) =>
        PaymentApproved.For(
            Now,
            Guid.CreateVersion7(Now),
            serviceAccountId,
            Guid.CreateVersion7(Now),
            billId,
            amount,
            "USD",
            "card",
            "sandbox-1");

    [Fact]
    public async Task An_approved_payment_is_offered_to_the_account_it_credits()
    {
        var arrangements = new RecordingArrangements();
        var account = Guid.CreateVersion7(Now);

        await ArrangementPayments.ApplyAsync(arrangements, AnApproval(account, 120.00m), CancellationToken.None);

        var recorded = Assert.Single(arrangements.Recorded);

        Assert.Equal(account, recorded.Account);
        Assert.Equal(120.00m, recorded.Amount);
        Assert.Equal("sandbox-1", recorded.Reference);
    }

    [Fact]
    public async Task A_payment_taken_against_no_particular_bill_still_counts_towards_the_promise()
    {
        // AN ARRANGEMENT IS ABOUT THE ARREARS AS A WHOLE. Billing's own consumer of this same event
        // skips a payment naming no bill, because it has no document to reduce; this one does not,
        // because a customer who rings up and pays $120 has kept this month's instalment whichever
        // receipt the money lands on.
        var arrangements = new RecordingArrangements();
        var account = Guid.CreateVersion7(Now);

        await ArrangementPayments.ApplyAsync(arrangements, AnApproval(account, 120.00m, billId: null), CancellationToken.None);

        Assert.Single(arrangements.Recorded);
    }

    [Fact]
    public void The_consumer_name_is_distinct_from_the_other_claimants_of_this_event() =>
        // Billing and Finance claim PaymentApproved too, each with its own work to do. A shared
        // dedupe name would mean whichever handled it first silently suppressed the others.
        Assert.Equal("customers.payment-approved", ArrangementPaymentApprovedConsumer.Name);
}
