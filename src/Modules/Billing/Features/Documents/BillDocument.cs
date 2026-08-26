using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Billing.Features.Documents;

/// <summary>One line of the bill, exactly as it was printed.</summary>
/// <param name="Sequence">Position on the bill, from 1.</param>
/// <param name="Kind">The standing charge, a consumption block from one tier of the tariff, or a fee.</param>
/// <param name="Description">What the line says.</param>
/// <param name="TierSequence">Which tier of the tariff produced it. Absent on a fee.</param>
/// <param name="Units">Units charged.</param>
/// <param name="RatePerUnit">Price of one unit inside that tier.</param>
/// <param name="Amount">What the line came to.</param>
public sealed record BillDocumentLine(
    int Sequence,
    string Kind,
    string Description,
    int? TierSequence,
    decimal? Units,
    decimal? RatePerUnit,
    decimal Amount);

/// <summary>
/// One correction made to the bill after it was issued, shown beneath the document rather than in it.
/// </summary>
/// <param name="Sequence">Position in the bill's adjustment history, from 1.</param>
/// <param name="Kind">Money off the bill, or money on to it.</param>
/// <param name="Amount">The signed change to what is owed. Negative on a credit.</param>
/// <param name="AmountDueAfter">What the bill came to once it was applied.</param>
/// <param name="Reason">Why it was made.</param>
/// <param name="ActorName">Who made it, for the copy that goes out.</param>
/// <param name="RecordedAt">When it was made.</param>
public sealed record BillDocumentCorrection(
    int Sequence,
    string Kind,
    decimal Amount,
    decimal AmountDueAfter,
    string Reason,
    string? ActorName,
    DateTimeOffset RecordedAt);

/// <summary>
/// An issued bill as the document the customer was sent — reproduced from what was stored, never
/// recalculated (WP-2.14). A charge bill reprints the same way, with no tariff and no meter on it:
/// the fee lines carry the figures the schedule published on the day they were raised.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is computed from a tariff, and that is the whole package.</b> Every figure is a
/// column on the bill or on one of its lines. Re-running <c>RateEngine</c> to produce a reprint
/// would mean handing a customer a document that disagrees with the one in their hand the moment a
/// rate changes, a tier is corrected, or a rounding rule is tightened — and it is precisely the
/// disputed bills, the ones whose tariff has since been fixed, that get reprinted. Bill already
/// stamps the account number, the customer's name, the meter, the dials and the tariff's code and
/// version onto itself for this reason; this type is what spends that.
/// </para>
/// <para>
/// <b>Corrections are shown separately, never folded into the lines.</b> <see cref="PrintedTotal"/>
/// keeps saying what the rate engine produced and what the customer holds a copy of;
/// <see cref="AmountDue"/> is that plus every correction since. Netting a credit into the
/// consumption line it relates to would produce a document that has never existed and that the
/// customer cannot reconcile against theirs — the same call WP-2.4 made when it refused to let an
/// adjustment rewrite a total.
/// </para>
/// <para>
/// <b>It refuses to print a document that does not add up.</b> Two guards, both of them restatements
/// of rules the aggregate already enforces: the lines must equal the printed total (as
/// <c>Bill.Calculate</c> insists) and the corrections in hand must equal the running adjustment
/// total (as <c>Bill.Adjust</c> insists). The second is the one that matters here — a bill loaded
/// without its adjustment history would otherwise reprint with a correct total, a short list of
/// corrections and an amount due that quietly disagrees with both.
/// </para>
/// </remarks>
/// <param name="BillId">Identifier of the bill this reproduces.</param>
/// <param name="BillNumber">The number printed on it.</param>
/// <param name="ServiceAccountId">The account billed.</param>
/// <param name="AccountNumber">Its number, as printed.</param>
/// <param name="CustomerId">Who owes it.</param>
/// <param name="CustomerName">Their name <b>at the time it was raised</b>, not today's.</param>
/// <param name="ServiceLocationId">The premise supplied.</param>
/// <param name="Kind">What the bill was raised for — a period of supply, or fees alone.</param>
/// <param name="RatePlanCode">The tariff it was priced on. Absent on a charge bill.</param>
/// <param name="RatePlanName">Its name, as printed. Absent on a charge bill.</param>
/// <param name="RatePlanEffectiveFrom">The version of it — why these rates and not others. Absent on a charge bill.</param>
/// <param name="Currency">ISO 4217 code every amount is expressed in.</param>
/// <param name="UnitOfMeasure">What the units are measured in. Absent on a charge bill.</param>
/// <param name="PeriodStart">First day of the billed period.</param>
/// <param name="PeriodEnd">Last day of it — the day the meter was read.</param>
/// <param name="MeterNumber">The meter that produced the reading. Absent on a charge bill.</param>
/// <param name="PreviousReading">The dials at the start of the period.</param>
/// <param name="CurrentReading">The dials at the end of it.</param>
/// <param name="Consumption">Units billed.</param>
/// <param name="Lines">The lines, in order, as printed.</param>
/// <param name="PrintedTotal">What the document said. Never moves once the bill is calculated.</param>
/// <param name="Corrections">Corrections made since it was issued, oldest first.</param>
/// <param name="CorrectionTotal">Their signed sum.</param>
/// <param name="AmountDue">What is owed today — the printed total plus those corrections.</param>
/// <param name="AmountPaid">How much has been paid against it, by cash or out of a deposit.</param>
/// <param name="Balance">What is still owed.</param>
/// <param name="Status">Where the bill stands today.</param>
/// <param name="IssuedOn">The day it went out.</param>
/// <param name="DueDate">The day payment falls due.</param>
/// <param name="ProducedAt">When this copy was produced.</param>
/// <param name="ProducedById">Subject id of whoever produced it — the audit entry names them too.</param>
/// <param name="ProducedByName">Their display name at the time.</param>
public sealed record BillDocument(
    Guid BillId,
    string BillNumber,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    string CustomerName,
    Guid ServiceLocationId,
    string Kind,
    string? RatePlanCode,
    string? RatePlanName,
    DateOnly? RatePlanEffectiveFrom,
    string Currency,
    string? UnitOfMeasure,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string? MeterNumber,
    decimal? PreviousReading,
    decimal? CurrentReading,
    decimal Consumption,
    IReadOnlyList<BillDocumentLine> Lines,
    decimal PrintedTotal,
    IReadOnlyList<BillDocumentCorrection> Corrections,
    decimal CorrectionTotal,
    decimal AmountDue,
    decimal AmountPaid,
    decimal Balance,
    string Status,
    DateOnly IssuedOn,
    DateOnly? DueDate,
    DateTimeOffset ProducedAt,
    string ProducedById,
    string? ProducedByName)
{
    /// <summary>
    /// Reproduces <paramref name="bill"/> as the document it was issued as.
    /// </summary>
    /// <param name="bill">The bill, loaded <b>with its lines and its whole adjustment history</b>.</param>
    /// <param name="actor">Who is producing this copy.</param>
    /// <param name="producedAt">When.</param>
    /// <exception cref="BillingWorkflowException">
    /// The bill was never issued. A draft is a working figure, not a document anybody was sent — and
    /// handing one to a customer is how a bill that was later re-run reaches them twice.
    /// </exception>
    /// <exception cref="BillingValidationException">
    /// The stored figures do not agree with each other: the lines do not add up to the printed
    /// total, or the bill was loaded without all of its corrections.
    /// </exception>
    public static BillDocument Of(Bill bill, RegistryActor actor, DateTimeOffset producedAt)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(actor);

        if (bill.IssuedOn is not { } issuedOn)
        {
            throw new BillingWorkflowException(
                $"Bill {bill.BillNumber} is {bill.Status} and has never been issued, so there is no document to reproduce. "
                + "A draft is corrected by billing it again, not by sending it out.");
        }

        // THE FIRST GUARD, and a restatement of Bill.Calculate's. Refused rather than corrected: a
        // printed total silently replaced by the sum of the lines would hide whatever produced the
        // disagreement, on a document about money that is about to leave the building.
        var printed = Money.Total(bill.Lines.Select(line => line.Amount));

        if (printed != bill.TotalAmount)
        {
            throw new BillingValidationException(
                $"Bill {bill.BillNumber} was issued for {bill.TotalAmount} but its stored lines add up to {printed}. "
                + "A reprint reproduces what was stored, so a bill that no longer agrees with itself is not reprinted.");
        }

        // THE SECOND GUARD, and the one this type exists to make. A bill loaded without its
        // adjustments carries a running AdjustmentTotal and an empty list, which would reprint as a
        // correct-looking document whose corrections section is missing the credit the customer rang
        // up about. Bill.Adjust refuses to write under exactly these conditions; this refuses to
        // print under them.
        var applied = Money.Total(bill.Adjustments.Select(adjustment => adjustment.Amount));

        if (applied != bill.AdjustmentTotal)
        {
            throw new BillingValidationException(
                $"Bill {bill.BillNumber} carries corrections totalling {bill.AdjustmentTotal} but only {applied} of them are loaded. "
                + "A bill is reprinted with its whole history in hand.");
        }

        return new BillDocument(
            bill.Id,
            bill.BillNumber,
            bill.ServiceAccountId,
            bill.AccountNumber,
            bill.CustomerId,

            // The name AS BILLED. A customer who has since married, or whose account has been put in
            // a company's name, still had this bill sent to the name that is on it — and a reprint
            // that quietly updated it would be a different document.
            bill.CustomerName,
            bill.ServiceLocationId,
            bill.Kind.ToString(),
            bill.RatePlanCode,
            bill.RatePlanName,
            bill.RatePlanEffectiveFrom,
            bill.Currency,
            bill.UnitOfMeasure,
            bill.PeriodStart,
            bill.PeriodEnd,
            bill.MeterNumber,
            bill.PreviousReading,
            bill.CurrentReading,
            bill.Consumption,
            [.. bill.Lines
                .OrderBy(line => line.Sequence)
                .Select(line => new BillDocumentLine(
                    line.Sequence,
                    line.Kind.ToString(),
                    line.Description,
                    line.TierSequence,
                    line.Units,
                    line.RatePerUnit,
                    line.Amount))],
            bill.TotalAmount,
            [.. bill.Adjustments
                .OrderBy(adjustment => adjustment.Sequence)
                .Select(adjustment => new BillDocumentCorrection(
                    adjustment.Sequence,
                    adjustment.Kind.ToString(),
                    adjustment.Amount,
                    adjustment.AmountDueAfter,
                    adjustment.Reason,
                    adjustment.ActorName,
                    adjustment.RecordedAt))],
            bill.AdjustmentTotal,
            bill.AmountDue,
            bill.AmountPaid,
            bill.Balance,
            bill.Status.ToString(),
            issuedOn,
            bill.DueDate,
            producedAt,
            actor.Id,
            actor.Name);
    }
}
