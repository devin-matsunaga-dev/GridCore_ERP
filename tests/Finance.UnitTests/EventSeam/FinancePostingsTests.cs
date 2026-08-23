using GridCore.Contracts.Events;
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
