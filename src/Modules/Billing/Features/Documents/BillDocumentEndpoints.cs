using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Billing.Features.Documents;

/// <summary>One line of a reprinted bill, as the API returns it.</summary>
/// <param name="Sequence">Position on the bill, from 1.</param>
/// <param name="Kind">The standing charge, a consumption block from one tier, or a fee.</param>
/// <param name="Description">What the line says.</param>
/// <param name="TierSequence">Which tier produced it.</param>
/// <param name="Units">Units charged.</param>
/// <param name="RatePerUnit">Price of one unit inside that tier.</param>
/// <param name="Amount">What the line came to.</param>
public sealed record BillDocumentLineResponse(
    int Sequence,
    string Kind,
    string Description,
    int? TierSequence,
    decimal? Units,
    decimal? RatePerUnit,
    decimal Amount)
{
    /// <summary>Projects a line for the wire.</summary>
    public static BillDocumentLineResponse From(BillDocumentLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new BillDocumentLineResponse(
            line.Sequence,
            line.Kind,
            line.Description,
            line.TierSequence,
            line.Units,
            line.RatePerUnit,
            line.Amount);
    }
}

/// <summary>One correction shown beneath a reprinted bill, as the API returns it.</summary>
/// <param name="Sequence">Position in the bill's adjustment history, from 1.</param>
/// <param name="Kind">Money off the bill, or money on to it.</param>
/// <param name="Amount">The signed change to what is owed.</param>
/// <param name="AmountDueAfter">What the bill came to once it was applied.</param>
/// <param name="Reason">Why it was made.</param>
/// <param name="ActorName">Who made it.</param>
/// <param name="RecordedAt">When it was made.</param>
public sealed record BillDocumentCorrectionResponse(
    int Sequence,
    string Kind,
    decimal Amount,
    decimal AmountDueAfter,
    string Reason,
    string? ActorName,
    DateTimeOffset RecordedAt)
{
    /// <summary>Projects a correction for the wire.</summary>
    public static BillDocumentCorrectionResponse From(BillDocumentCorrection correction)
    {
        ArgumentNullException.ThrowIfNull(correction);

        return new BillDocumentCorrectionResponse(
            correction.Sequence,
            correction.Kind,
            correction.Amount,
            correction.AmountDueAfter,
            correction.Reason,
            correction.ActorName,
            correction.RecordedAt);
    }
}

/// <summary>A reprinted bill, as the API returns it.</summary>
/// <param name="BillId">Identifier of the bill this reproduces.</param>
/// <param name="BillNumber">The number printed on it.</param>
/// <param name="ServiceAccountId">The account billed.</param>
/// <param name="AccountNumber">Its number, as printed.</param>
/// <param name="CustomerId">Who owes it.</param>
/// <param name="CustomerName">Their name at the time it was raised.</param>
/// <param name="ServiceLocationId">The premise supplied.</param>
/// <param name="Kind">What the bill was raised for — a period of supply, or fees alone.</param>
/// <param name="RatePlanCode">The tariff it was priced on. Absent on a charge bill.</param>
/// <param name="RatePlanName">Its name.</param>
/// <param name="RatePlanEffectiveFrom">The version of it.</param>
/// <param name="Currency">ISO 4217 code every amount is expressed in.</param>
/// <param name="UnitOfMeasure">What the units are measured in.</param>
/// <param name="PeriodStart">First day of the billed period.</param>
/// <param name="PeriodEnd">Last day of it.</param>
/// <param name="MeterNumber">The meter read. Absent on a charge bill.</param>
/// <param name="PreviousReading">The dials at the start of the period.</param>
/// <param name="CurrentReading">The dials at the end of it.</param>
/// <param name="Consumption">Units billed.</param>
/// <param name="Lines">The lines, as printed.</param>
/// <param name="PrintedTotal">What the document said.</param>
/// <param name="Corrections">Corrections made since, shown separately.</param>
/// <param name="CorrectionTotal">Their signed sum.</param>
/// <param name="AmountDue">What is owed today.</param>
/// <param name="AmountPaid">How much has been paid.</param>
/// <param name="Balance">What is still owed.</param>
/// <param name="Status">Where the bill stands today.</param>
/// <param name="IssuedOn">The day it went out.</param>
/// <param name="DueDate">The day payment falls due.</param>
/// <param name="ProducedAt">When this copy was produced.</param>
/// <param name="ProducedById">Subject id of whoever produced it.</param>
/// <param name="ProducedByName">Their display name.</param>
public sealed record BillDocumentResponse(
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
    IReadOnlyList<BillDocumentLineResponse> Lines,
    decimal PrintedTotal,
    IReadOnlyList<BillDocumentCorrectionResponse> Corrections,
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
    /// <summary>Projects a document for the wire.</summary>
    public static BillDocumentResponse From(BillDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new BillDocumentResponse(
            document.BillId,
            document.BillNumber,
            document.ServiceAccountId,
            document.AccountNumber,
            document.CustomerId,
            document.CustomerName,
            document.ServiceLocationId,
            document.Kind,
            document.RatePlanCode,
            document.RatePlanName,
            document.RatePlanEffectiveFrom,
            document.Currency,
            document.UnitOfMeasure,
            document.PeriodStart,
            document.PeriodEnd,
            document.MeterNumber,
            document.PreviousReading,
            document.CurrentReading,
            document.Consumption,
            [.. document.Lines.Select(BillDocumentLineResponse.From)],
            document.PrintedTotal,
            [.. document.Corrections.Select(BillDocumentCorrectionResponse.From)],
            document.CorrectionTotal,
            document.AmountDue,
            document.AmountPaid,
            document.Balance,
            document.Status,
            document.IssuedOn,
            document.DueDate,
            document.ProducedAt,
            document.ProducedById,
            document.ProducedByName);
    }
}

/// <summary>The bill reprint's HTTP surface (WP-2.14).</summary>
/// <remarks>
/// <para>
/// <b>A GET, though it writes an audit entry.</b> The package is read-side: nothing about the bill
/// moves and asking twice gives the same document, which is what a GET promises. The entry it leaves
/// records that a copy was produced, and a POST would instead say a <i>reprint</i> is a resource the
/// utility keeps — which would then need a register, a list and an id, none of which anybody asked
/// for.
/// </para>
/// <para>
/// <b>A sub-resource noun rather than a verb.</b> <c>/document</c> is the thing the bill has;
/// <c>/reprint</c> would be the act, and CONVENTIONS.md puts acts behind POST. What the audit trail
/// calls it — <c>bill.reprint</c> — is the act, correctly, because that is what happened.
/// </para>
/// <para>
/// Gated on <c>customers.documents</c>, not on <c>billing.read</c>: reading the register and handing
/// a customer a copy of a document are different acts. A billing officer holds both.
/// </para>
/// </remarks>
public static class BillDocumentEndpoints
{
    /// <summary>Route of one bill's document.</summary>
    public const string DocumentRoute = "/api/bills/{billId:guid}/document";

    /// <summary>Maps the bill document endpoints.</summary>
    public static IEndpointRouteBuilder MapBillDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet(DocumentRoute, ([FromRoute] Guid billId, [FromServices] IBillDocumentService documents, CancellationToken cancellationToken) =>
                BillingProblems.RunAsync(async () =>
                    Results.Ok(BillDocumentResponse.From(await documents.ReprintAsync(billId, cancellationToken)))))
            .RequirePermission(Permissions.Customers.Documents)
            .WithTags("Billing")
            .WithName("ReprintBill");

        return endpoints;
    }
}
