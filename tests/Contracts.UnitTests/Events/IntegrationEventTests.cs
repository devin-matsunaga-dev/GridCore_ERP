using GridCore.Contracts.Events;

namespace GridCore.Contracts.UnitTests.Events;

/// <summary>
/// The cross-module vocabulary. Two properties matter for every event: it carries an identity a
/// consumer can deduplicate on, and money on it is <see langword="decimal"/> and exact.
/// </summary>
public sealed class IntegrationEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Bill_issued_carries_a_version_7_identity_stamped_from_when_it_happened()
    {
        var issued = NewBillIssued(184.55m);

        Assert.Equal(7, issued.EventId.Version);
        Assert.Equal(Now, issued.OccurredAt);
        Assert.NotEqual(Guid.Empty, issued.EventId);
    }

    [Fact]
    public void Events_created_in_order_sort_in_order_by_identity()
    {
        var earlier = NewBillIssued(10m);
        var later = BillIssued.For(
            Now.AddMinutes(1),
            Guid.CreateVersion7(),
            "B-000124",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            new DateOnly(2026, 8, 20),
            10m,
            "USD");

        // Guid v7 sorts chronologically, so an event log ordered by key is ordered by time — the
        // same property the audit trail relies on.
        Assert.True(string.CompareOrdinal(earlier.EventId.ToString(), later.EventId.ToString()) < 0);
    }

    [Fact]
    public void Bill_amounts_are_exact_decimals()
    {
        var issued = NewBillIssued(0.1m + 0.2m);

        // 0.30 exactly. This is why money is decimal and never double.
        Assert.Equal(0.3m, issued.Amount);
    }

    [Fact]
    public void Bill_adjusted_states_a_signed_change_rather_than_a_new_total()
    {
        // What lets Finance post a correction as a second balanced entry instead of going back and
        // rewriting the first (invariant 3). A credit is negative; the kind is there to be read.
        var adjusted = BillAdjusted.For(
            Now,
            billId: Guid.CreateVersion7(),
            billNumber: "B-000123",
            serviceAccountId: Guid.CreateVersion7(),
            customerId: Guid.CreateVersion7(),
            adjustmentId: Guid.CreateVersion7(),
            kind: "Credit",
            amount: -20.35m,
            amountDue: 164.20m,
            currency: "USD",
            reason: "Estimated read corrected.");

        Assert.Equal(-20.35m, adjusted.Amount);
        Assert.Equal(164.20m, adjusted.AmountDue);
        Assert.Equal("Credit", adjusted.Kind);
        Assert.Equal(7, adjusted.EventId.Version);
        Assert.Equal(Now, adjusted.OccurredAt);
    }

    [Fact]
    public void An_adjustment_and_the_bill_it_corrects_are_two_different_facts()
    {
        // Same bill, two events, two identities — so a consumer deduplicating on EventId cannot
        // mistake the correction for a redelivery of the issue.
        var bill = Guid.CreateVersion7();

        var issued = BillIssued.For(
            Now,
            bill,
            "B-000123",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            new DateOnly(2026, 8, 20),
            184.55m,
            "USD");

        var adjusted = BillAdjusted.For(
            Now,
            bill,
            "B-000123",
            issued.ServiceAccountId,
            issued.CustomerId,
            Guid.CreateVersion7(),
            "Credit",
            -20.35m,
            164.20m,
            "USD",
            "Estimated read corrected.");

        Assert.Equal(issued.BillId, adjusted.BillId);
        Assert.NotEqual(issued.EventId, adjusted.EventId);

        // And the two add up: what was billed plus the correction is what is now owed.
        Assert.Equal(issued.Amount + adjusted.Amount, adjusted.AmountDue);
    }

    [Fact]
    public void Payment_approved_carries_the_provider_reference_for_reconciliation()
    {
        var approved = PaymentApproved.For(
            Now,
            paymentId: Guid.CreateVersion7(),
            serviceAccountId: Guid.CreateVersion7(),
            customerId: Guid.CreateVersion7(),
            billId: null,
            amount: 75.20m,
            currency: "USD",
            method: "card",
            providerReference: "SIM-8842");

        Assert.Equal("SIM-8842", approved.ProviderReference);
        Assert.Null(approved.BillId);
        Assert.Equal(7, approved.EventId.Version);
    }

    [Fact]
    public void Goods_received_totals_its_lines()
    {
        var received = GoodsReceived.For(
            Now,
            receiptId: Guid.CreateVersion7(),
            purchaseOrderId: Guid.CreateVersion7(),
            warehouseId: Guid.CreateVersion7(),
            vendorId: Guid.CreateVersion7(),
            currency: "USD",
            lines:
            [
                new GoodsReceivedLine(Guid.CreateVersion7(), "TRF-100", 3m, 249.99m),
                new GoodsReceivedLine(Guid.CreateVersion7(), "CBL-050", 12m, 15.25m),
            ]);

        // 3 x 249.99 + 12 x 15.25 = 749.97 + 183.00
        Assert.Equal(932.97m, received.TotalCost);
        Assert.Equal(749.97m, received.Lines[0].LineCost);
    }

    [Fact]
    public void Goods_received_refuses_to_total_nothing() =>
        Assert.Throws<ArgumentNullException>(() => GoodsReceived.For(
            Now,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "USD",
            lines: null!));

    private static BillIssued NewBillIssued(decimal amount) => BillIssued.For(
        Now,
        billId: Guid.CreateVersion7(),
        billNumber: "B-000123",
        serviceAccountId: Guid.CreateVersion7(),
        customerId: Guid.CreateVersion7(),
        periodStart: new DateOnly(2026, 7, 1),
        periodEnd: new DateOnly(2026, 7, 31),
        dueDate: new DateOnly(2026, 8, 20),
        amount: amount,
        currency: "USD");
}
