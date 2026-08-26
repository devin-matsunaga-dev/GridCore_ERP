using GridCore.Contracts.Events;

namespace GridCore.Contracts.UnitTests.Events;

/// <summary>
/// WP-2.12's three deposit facts, as the cross-module vocabulary holds them.
/// </summary>
/// <remarks>
/// A deposit is a series of immutable movements, so each event names the <b>entry</b> and states an
/// amount — never "the balance is now X", which is a snapshot two redeliveries could disagree about.
/// The balance rides along as a fact about that moment, for a consumer that wants to render it; it
/// is not what the posting is computed from.
/// </remarks>
public sealed class DepositEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Every_deposit_event_carries_a_version_7_identity_stamped_from_when_it_happened() =>
        Assert.All(
            new IIntegrationEvent[] { Collected(75.00m), Applied(40.00m), Refunded(35.00m) },
            @event =>
            {
                Assert.Equal(7, @event.EventId.Version);
                Assert.Equal(Now, @event.OccurredAt);
            });

    [Fact]
    public void Deposit_amounts_are_exact_decimals() =>
        // 0.30 exactly. This is why money is decimal and never double, and a deposit is the balance
        // a refund years later has to reconcile against.
        Assert.Equal(0.3m, Collected(0.1m + 0.2m).Amount);

    [Fact]
    public void Every_deposit_movement_is_stated_as_a_positive_amount()
    {
        // The direction lives in the event's TYPE, not in a sign: a refund is not a negative
        // collection, and Finance posts the magnitude on the correct side rather than a negative
        // debit — the rule a bill credit already follows.
        Assert.True(Collected(75.00m).Amount > 0m);
        Assert.True(Applied(40.00m).Amount > 0m);
        Assert.True(Refunded(35.00m).Amount > 0m);
    }

    [Fact]
    public void An_application_names_the_bill_and_the_account_it_settled()
    {
        // Both are needed downstream and neither is derivable: Billing reduces the bill, and
        // Finance keys the AR relief on the service account. A consumer that had to ask Customers
        // for either would be a consumer calling back upstream.
        var applied = Applied(40.00m);

        Assert.NotEqual(Guid.Empty, applied.BillId);
        Assert.NotEqual(Guid.Empty, applied.ServiceAccountId);
        Assert.Equal("BIL-000123", applied.BillNumber);
    }

    [Fact]
    public void A_collection_carries_the_terms_it_was_taken_under() =>
        // Stored, never accrued in the MVP. Recording them now is what stops a later package having
        // to guess retrospectively which deposits were interest-bearing.
        Assert.True(Collected(75.00m, interestBearing: true).IsInterestBearing);

    [Fact]
    public void A_refund_carries_why_it_was_given_back() =>
        // The one deposit movement that takes money out of the building, so the reason travels with
        // the fact rather than staying behind in the module that raised it.
        Assert.Equal("Account closed.", Refunded(35.00m).Reason);

    private static CustomerDepositCollected Collected(decimal amount, bool interestBearing = false) =>
        CustomerDepositCollected.For(
            Now,
            depositEntryId: Guid.CreateVersion7(Now),
            customerId: Guid.CreateVersion7(Now),
            accountNumber: "C-000123",
            amount: amount,
            balanceAfter: amount,
            currency: "USD",
            isInterestBearing: interestBearing);

    private static CustomerDepositApplied Applied(decimal amount) =>
        CustomerDepositApplied.For(
            Now,
            depositEntryId: Guid.CreateVersion7(Now),
            customerId: Guid.CreateVersion7(Now),
            serviceAccountId: Guid.CreateVersion7(Now),
            billId: Guid.CreateVersion7(Now),
            billNumber: "BIL-000123",
            amount: amount,
            balanceAfter: 35.00m,
            currency: "USD");

    private static CustomerDepositRefunded Refunded(decimal amount) =>
        CustomerDepositRefunded.For(
            Now,
            depositEntryId: Guid.CreateVersion7(Now),
            customerId: Guid.CreateVersion7(Now),
            accountNumber: "C-000123",
            amount: amount,
            balanceAfter: 0m,
            currency: "USD",
            reason: "Account closed.");
}
