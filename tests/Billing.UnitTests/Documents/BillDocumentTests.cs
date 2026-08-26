using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Documents;
using GridCore.Modules.Billing.Features.Rating;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Modules.Billing.UnitTests.Infrastructure;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Billing.UnitTests.Documents;

/// <summary>
/// The reprint itself (WP-2.14), with no database anywhere near it.
/// </summary>
/// <remarks>
/// The claim under test is the one a customer would dispute: <b>a reprint reproduces the bill as it
/// was issued</b>. Every figure comes off a stored column, corrections are shown separately rather
/// than folded into the lines they correct, and a document whose stored figures no longer agree with
/// each other is refused rather than printed.
/// </remarks>
public class BillDocumentTests
{
    private static readonly DateTimeOffset Issued = new(2026, 7, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 11, 30, 0, TimeSpan.Zero);
    private static readonly RegistryActor Clerk = new("auth0|clerk", "Ana Cruz");

    /// <summary>A bill of two lines — a standing charge and a tier — priced at 15.00 + 100.00.</summary>
    private static Bill ABill(string customerName = "Ana Cruz")
    {
        var account = new ServiceAccountSummary(
            Guid.CreateVersion7(Issued),
            "A-000001",
            Guid.CreateVersion7(Issued),
            customerName,
            Guid.CreateVersion7(Issued),
            "Open",
            HoldsPremise: true,
            ServiceStartedAt: Issued.AddMonths(-6));

        var calculation = new RateCalculation(
            Guid.CreateVersion7(Issued),
            "RES-STD",
            "Residential standard",
            new DateOnly(2026, 1, 1),
            "USD",
            "kWh",
            400m,
            [
                new RateCharge(1, ChargeKind.ServiceCharge, "Standing charge", null, null, null, 15.00m),
                new RateCharge(2, ChargeKind.Consumption, "First 400 kWh", 1, 400m, 0.25m, 100.00m),
            ],
            115.00m);

        return Bill.Calculate(
            "BIL-000001",
            account,
            new BilledReading(Guid.CreateVersion7(Issued), Guid.CreateVersion7(Issued), "MTR-9001", 1_000m, 1_400m),
            calculation,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            Clerk,
            Issued);
    }

    private static Bill AnIssuedBill(string customerName = "Ana Cruz")
    {
        var bill = ABill(customerName);

        bill.Issue(new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 26), Clerk, Issued);

        return bill;
    }

    [Fact]
    public void A_reprint_reproduces_what_the_document_said()
    {
        var bill = AnIssuedBill();

        var document = BillDocument.Of(bill, Clerk, Now);

        Assert.Equal("BIL-000001", document.BillNumber);
        Assert.Equal(115.00m, document.PrintedTotal);
        Assert.Equal(new DateOnly(2026, 7, 5), document.IssuedOn);
        Assert.Equal("RES-STD", document.RatePlanCode);
        Assert.Equal("MTR-9001", document.MeterNumber);
        Assert.Equal(1_000m, document.PreviousReading);
        Assert.Equal(1_400m, document.CurrentReading);
        Assert.Equal(400m, document.Consumption);

        Assert.Collection(
            document.Lines,
            line => Assert.Equal(("Standing charge", 15.00m), (line.Description, line.Amount)),
            line => Assert.Equal(("First 400 kWh", 100.00m), (line.Description, line.Amount)));
    }

    [Fact]
    public void An_ADJUSTED_bill_reprints_AS_ISSUED_with_its_corrections_shown_separately()
    {
        // THE PACKAGE'S HEADLINE CLAIM. The customer is holding a bill for 115.00; a credit of 20.00
        // has been granted since. The reprint must still say 115.00 and must say so in the lines the
        // customer can check, with the credit beneath as a correction on its own dated row.
        var bill = AnIssuedBill();

        bill.Adjust(BillAdjustmentKind.Credit, 20.00m, "Meter misread", Clerk, Now);

        var document = BillDocument.Of(bill, Clerk, Now);

        Assert.Equal(115.00m, document.PrintedTotal);
        Assert.Equal(115.00m, document.Lines.Sum(line => line.Amount));
        Assert.Equal(-20.00m, document.CorrectionTotal);
        Assert.Equal(95.00m, document.AmountDue);

        var correction = Assert.Single(document.Corrections);

        Assert.Equal("Credit", correction.Kind);
        Assert.Equal(-20.00m, correction.Amount);
        Assert.Equal(95.00m, correction.AmountDueAfter);
        Assert.Equal("Meter misread", correction.Reason);

        // And nothing about the lines moved. This is the assertion that fails the day somebody
        // "helpfully" nets a credit into the consumption line it relates to.
        Assert.Collection(
            document.Lines,
            line => Assert.Equal(15.00m, line.Amount),
            line => Assert.Equal(100.00m, line.Amount));
    }

    [Fact]
    public void The_name_on_the_document_is_the_name_it_was_BILLED_in()
    {
        // A customer who has since married still had this bill sent to the name printed on it. The
        // bill stamps the name at the time (WP-2.3); the reprint spends that rather than resolving
        // today's, which would be a different document.
        var document = BillDocument.Of(AnIssuedBill("Ana Reyes"), Clerk, Now);

        Assert.Equal("Ana Reyes", document.CustomerName);
    }

    [Fact]
    public void A_reprint_records_who_produced_it_and_when()
    {
        var document = BillDocument.Of(AnIssuedBill(), Clerk, Now);

        Assert.Equal(Now, document.ProducedAt);
        Assert.Equal("auth0|clerk", document.ProducedById);
        Assert.Equal("Ana Cruz", document.ProducedByName);
    }

    [Fact]
    public void A_paid_bill_still_reprints_and_says_so()
    {
        var bill = AnIssuedBill();

        bill.RecordPayment(115.00m, Clerk, Now);

        var document = BillDocument.Of(bill, Clerk, Now);

        Assert.Equal(nameof(BillStatus.Paid), document.Status);
        Assert.Equal(115.00m, document.PrintedTotal);
        Assert.Equal(115.00m, document.AmountPaid);
        Assert.Equal(0m, document.Balance);
    }

    [Fact]
    public void A_DRAFT_is_not_a_document()
    {
        // THE FAILURE PATH. A draft is a working figure nobody was sent; handing one to a customer
        // is how a bill that was later re-run reaches them twice.
        var error = Assert.Throws<BillingWorkflowException>(() => BillDocument.Of(ABill(), Clerk, Now));

        Assert.Contains("never been issued", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bill_loaded_without_its_corrections_is_not_reprinted()
    {
        // THE OTHER FAILURE PATH, and the reason the guard exists. A bill fetched without its
        // adjustment history carries a running correction total and an empty list — it would print a
        // correct-looking document whose corrections section is missing the credit the customer rang
        // up about, with an amount due that agrees with neither half.
        var bill = AnIssuedBill();

        bill.Adjust(BillAdjustmentKind.Credit, 20.00m, "Meter misread", Clerk, Now);

        var reloaded = Detached(bill);

        var error = Assert.Throws<BillingValidationException>(() => BillDocument.Of(reloaded, Clerk, Now));

        Assert.Contains("whole history in hand", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same bill as EF would hand it back if a caller forgot to include its adjustments: the
    /// running totals are there and the collection is empty.
    /// </summary>
    private static Bill Detached(Bill bill)
    {
        var copy = AnIssuedBill();

        // Reflection rather than a second constructor. The alternative is an entity with a
        // "pretend you were loaded badly" factory on it, which is a hole in the aggregate kept open
        // for a test — the exact thing the private setters exist to prevent.
        typeof(Bill)
            .GetProperty(nameof(Bill.AdjustmentTotal))!
            .SetValue(copy, bill.AdjustmentTotal);

        return copy;
    }
}
