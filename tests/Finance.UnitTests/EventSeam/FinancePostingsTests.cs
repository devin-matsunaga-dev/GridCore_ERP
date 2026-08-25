using GridCore.Contracts.Events;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.EventSeam;

namespace GridCore.Modules.Finance.UnitTests.EventSeam;

/// <summary>
/// The event→journal mapping, verified without a bus, a broker or a database. Invariant 3 of
/// ARCHITECTURE.md — every financial transaction posts balanced entries — is asserted here in
/// milliseconds rather than discovered in a trial balance.
/// </summary>
public sealed class FinancePostingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void A_bill_debits_receivables_and_credits_revenue()
    {
        var issued = BillIssued.For(
            Now,
            billId: Guid.CreateVersion7(Now),
            billNumber: "B-000123",
            serviceAccountId: Guid.CreateVersion7(Now),
            customerId: Guid.CreateVersion7(Now),
            periodStart: new DateOnly(2026, 7, 1),
            periodEnd: new DateOnly(2026, 7, 31),
            dueDate: new DateOnly(2026, 8, 20),
            amount: 184.55m,
            currency: "USD");

        var posting = FinancePostings.From(issued);

        Assert.Equal(FinancePostings.BillIssuedSource, posting.Source);
        Assert.Equal("B-000123", posting.Reference);
        Assert.Equal(issued.EventId, posting.EventId);
        Assert.Equal(184.55m, DebitOf(posting, FinanceAccounts.AccountsReceivable));
        Assert.Equal(184.55m, CreditOf(posting, FinanceAccounts.Revenue));
        AssertBalances(posting, 184.55m);
    }

    [Fact]
    public void An_approved_payment_debits_cash_and_clears_the_receivable()
    {
        var approved = PaymentApproved.For(
            Now,
            paymentId: Guid.CreateVersion7(Now),
            serviceAccountId: Guid.CreateVersion7(Now),
            customerId: Guid.CreateVersion7(Now),
            billId: Guid.CreateVersion7(Now),
            amount: 75.20m,
            currency: "USD",
            method: "card",
            providerReference: "SIM-8842");

        var posting = FinancePostings.From(approved);

        Assert.Equal(FinancePostings.PaymentApprovedSource, posting.Source);
        Assert.Equal("SIM-8842", posting.Reference);
        Assert.Equal(75.20m, DebitOf(posting, FinanceAccounts.Cash));
        Assert.Equal(75.20m, CreditOf(posting, FinanceAccounts.AccountsReceivable));
        AssertBalances(posting, 75.20m);
    }

    [Fact]
    public void A_goods_receipt_debits_inventory_and_raises_a_payable()
    {
        var received = GoodsReceived.For(
            Now,
            receiptId: Guid.CreateVersion7(Now),
            purchaseOrderId: Guid.CreateVersion7(Now),
            warehouseId: Guid.CreateVersion7(Now),
            vendorId: Guid.CreateVersion7(Now),
            currency: "USD",
            lines:
            [
                new GoodsReceivedLine(Guid.CreateVersion7(Now), "TRF-100", 3m, 249.99m),
                new GoodsReceivedLine(Guid.CreateVersion7(Now), "CBL-050", 12m, 15.25m),
            ]);

        var posting = FinancePostings.From(received);

        Assert.Equal(FinancePostings.GoodsReceivedSource, posting.Source);
        Assert.Equal(932.97m, DebitOf(posting, FinanceAccounts.Inventory));
        Assert.Equal(932.97m, CreditOf(posting, FinanceAccounts.AccountsPayable));
        AssertBalances(posting, 932.97m);
    }

    [Fact]
    public void A_posting_that_does_not_balance_is_refused()
    {
        // Failure path, and the one that matters most: the ledger only ever holds balanced entries,
        // so an unbalanced mapping must fail where it is written, not where it is read.
        var thrown = Assert.Throws<ArgumentException>(() => JournalPostingIntent.For(
            Guid.CreateVersion7(Now),
            Now,
            "test.source",
            "REF-1",
            "A posting that does not balance",
            "USD",
            [
                JournalLineIntent.Debits(FinanceAccounts.AccountsReceivable, 100m),
                JournalLineIntent.Credits(FinanceAccounts.Revenue, 99.99m),
            ]));

        Assert.Contains("does not balance", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_posting_with_no_lines_is_refused() =>
        Assert.Throws<ArgumentException>(() => JournalPostingIntent.For(
            Guid.CreateVersion7(Now),
            Now,
            "test.source",
            "REF-1",
            "A posting with no lines",
            "USD",
            []));

    [Fact]
    public void Postings_stay_exact_at_awkward_amounts()
    {
        var issued = BillIssued.For(
            Now,
            Guid.CreateVersion7(Now),
            "B-000125",
            Guid.CreateVersion7(Now),
            Guid.CreateVersion7(Now),
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            new DateOnly(2026, 8, 20),
            amount: 0.1m + 0.2m,
            currency: "USD");

        var posting = FinancePostings.From(issued);

        // A float would leave the entry out by 0.00000000000000004 and the guard would reject it.
        Assert.Equal(0.3m, posting.TotalDebits);
        AssertBalances(posting, 0.3m);
    }

    [Fact]
    public void A_credit_to_a_bill_reverses_the_receivable_and_the_revenue()
    {
        var adjusted = BillAdjusted.For(
            Now,
            billId: Guid.CreateVersion7(Now),
            billNumber: "B-000123",
            serviceAccountId: Guid.CreateVersion7(Now),
            customerId: Guid.CreateVersion7(Now),
            adjustmentId: Guid.CreateVersion7(Now),
            kind: "Credit",
            amount: -40.00m,
            amountDue: 144.55m,
            currency: "USD",
            reason: "Estimated read corrected after a site visit.");

        var posting = FinancePostings.From(adjusted);

        // The exact reverse of issuing, and posted as a positive amount on the other side rather
        // than as a negative one: a negative debit and a credit are the same money, and only one of
        // them can be added up by eye.
        Assert.Equal(FinancePostings.BillAdjustedSource, posting.Source);
        Assert.Equal("B-000123", posting.Reference);
        Assert.Equal(40.00m, DebitOf(posting, FinanceAccounts.Revenue));
        Assert.Equal(40.00m, CreditOf(posting, FinanceAccounts.AccountsReceivable));
        Assert.Equal(0m, DebitOf(posting, FinanceAccounts.AccountsReceivable));
        AssertBalances(posting, 40.00m);
    }

    [Fact]
    public void A_charge_to_a_bill_posts_the_same_way_round_as_issuing_it()
    {
        var adjusted = BillAdjusted.For(
            Now,
            billId: Guid.CreateVersion7(Now),
            billNumber: "B-000124",
            serviceAccountId: Guid.CreateVersion7(Now),
            customerId: Guid.CreateVersion7(Now),
            adjustmentId: Guid.CreateVersion7(Now),
            kind: "Charge",
            amount: 12.30m,
            amountDue: 196.85m,
            currency: "USD",
            reason: "Read was too low; the difference is still owed.");

        var posting = FinancePostings.From(adjusted);

        Assert.Equal(12.30m, DebitOf(posting, FinanceAccounts.AccountsReceivable));
        Assert.Equal(12.30m, CreditOf(posting, FinanceAccounts.Revenue));
        AssertBalances(posting, 12.30m);
    }

    [Fact]
    public void A_correction_that_moves_no_money_is_refused()
    {
        // Failure path. Bill.Adjust refuses a zero adjustment, so this should be unreachable — which
        // is the point: if it ever stops being unreachable, an entry that moves nothing must not
        // quietly join the ledger, because it balances perfectly and explains nothing.
        var adjusted = BillAdjusted.For(
            Now,
            billId: Guid.CreateVersion7(Now),
            billNumber: "B-000125",
            serviceAccountId: Guid.CreateVersion7(Now),
            customerId: Guid.CreateVersion7(Now),
            adjustmentId: Guid.CreateVersion7(Now),
            kind: "Charge",
            amount: 0m,
            amountDue: 184.55m,
            currency: "USD",
            reason: "A correction that corrects nothing.");

        Assert.Throws<ArgumentException>(() => FinancePostings.From(adjusted));
    }

    [Fact]
    public void Every_posting_about_a_customer_carries_the_party_an_ar_view_needs()
    {
        var serviceAccountId = Guid.CreateVersion7(Now);
        var customerId = Guid.CreateVersion7(Now);

        var issued = FinancePostings.From(BillIssued.For(
            Now,
            Guid.CreateVersion7(Now),
            "B-000200",
            serviceAccountId,
            customerId,
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            new DateOnly(2026, 8, 20),
            100m,
            "USD"));

        var approved = FinancePostings.From(PaymentApproved.For(
            Now,
            Guid.CreateVersion7(Now),
            serviceAccountId,
            customerId,
            billId: null,
            amount: 100m,
            currency: "USD",
            method: "cash",
            providerReference: "SIM-1"));

        var adjusted = FinancePostings.From(BillAdjusted.For(
            Now,
            Guid.CreateVersion7(Now),
            "B-000200",
            serviceAccountId,
            customerId,
            Guid.CreateVersion7(Now),
            "Credit",
            -5m,
            95m,
            "USD",
            "Goodwill."));

        Assert.All(
            new[] { issued, approved, adjusted },
            posting =>
            {
                Assert.Equal(serviceAccountId, posting.ServiceAccountId);
                Assert.Equal(customerId, posting.CustomerId);
            });
    }

    [Fact]
    public void A_line_that_is_neither_a_debit_nor_a_credit_is_refused()
    {
        // Failure path: a line with money on both sides is two lines netted off, and netting is how
        // a ledger stops explaining itself while still balancing.
        var thrown = Assert.Throws<ArgumentException>(() => JournalPostingIntent.For(
            Guid.CreateVersion7(Now),
            Now,
            "test.source",
            "REF-1",
            "A two-sided line",
            "USD",
            [
                new JournalLineIntent(FinanceAccounts.AccountsReceivable, 100m, 100m),
                JournalLineIntent.Debits(FinanceAccounts.Cash, 0m),
            ]));

        Assert.Contains("exactly one side", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_amount_is_refused()
    {
        var thrown = Assert.Throws<ArgumentException>(() => JournalPostingIntent.For(
            Guid.CreateVersion7(Now),
            Now,
            "test.source",
            "REF-1",
            "A posting the wrong way round",
            "USD",
            [
                JournalLineIntent.Debits(FinanceAccounts.AccountsReceivable, -10m),
                JournalLineIntent.Credits(FinanceAccounts.Revenue, -10m),
            ]));

        Assert.Contains("negative", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_amount_finer_than_a_cent_is_refused()
    {
        // Refused, not rounded. The amounts arriving here were computed and rounded upstream, so one
        // that is finer than a cent means an upstream total that no longer adds up — and rounding it
        // here would hide exactly that.
        var thrown = Assert.Throws<ArgumentException>(() => JournalPostingIntent.For(
            Guid.CreateVersion7(Now),
            Now,
            "test.source",
            "REF-1",
            "A third of a dollar",
            "USD",
            [
                JournalLineIntent.Debits(FinanceAccounts.AccountsReceivable, 33.333m),
                JournalLineIntent.Credits(FinanceAccounts.Revenue, 33.333m),
            ]));

        Assert.Contains("finer than a cent", thrown.Message, StringComparison.Ordinal);
    }

    private static void AssertBalances(JournalPostingIntent posting, decimal expectedTotal)
    {
        Assert.Equal(expectedTotal, posting.TotalDebits);
        Assert.Equal(posting.TotalDebits, posting.TotalCredits);
    }

    private static decimal DebitOf(JournalPostingIntent posting, string accountCode) =>
        posting.Lines.Where(line => line.AccountCode == accountCode).Sum(line => line.Debit);

    private static decimal CreditOf(JournalPostingIntent posting, string accountCode) =>
        posting.Lines.Where(line => line.AccountCode == accountCode).Sum(line => line.Credit);
}
