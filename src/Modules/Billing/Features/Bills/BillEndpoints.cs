using GridCore.Modules.Billing.Features.Rating;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Billing.Features.Bills;

/// <summary>Body of a request to bill a reading cycle.</summary>
/// <param name="CycleCode">The reading cycle to bill, e.g. <c>2026-08</c>.</param>
public sealed record RunBillingRequest(string CycleCode);

/// <summary>Body of a request to issue a draft bill.</summary>
/// <param name="IssuedOn">The day it goes out. Defaults to today.</param>
/// <param name="DueDate">When payment falls due. Defaults to the standard term after the issue date.</param>
/// <param name="Reason">What to record against the transition.</param>
public sealed record IssueBillRequest(DateOnly? IssuedOn = null, DateOnly? DueDate = null, string? Reason = null);

/// <summary>Body of a request to withdraw a bill.</summary>
/// <param name="Reason">Why. Required — cancelling a bill removes money the utility was owed.</param>
public sealed record CancelBillRequest(string Reason);

/// <summary>Body of a request to correct an issued bill.</summary>
/// <param name="Kind">Which way the money moves — <c>Credit</c> off the bill, or <c>Charge</c> on to it.</param>
/// <param name="Amount">How much, always positive. The kind carries the direction, not the sign.</param>
/// <param name="Reason">Why. Required — this is the sensitive action invariant 5 is about.</param>
public sealed record AdjustBillRequest(BillAdjustmentKind Kind, decimal Amount, string Reason);

/// <summary>Body of a request to review overdue bills.</summary>
/// <param name="AsOf">The day to judge against. Defaults to today.</param>
public sealed record OverdueReviewRequest(DateOnly? AsOf = null);

/// <summary>One line of a bill, as the API returns it.</summary>
/// <param name="Sequence">Position on the bill, from 1.</param>
/// <param name="Kind">The standing charge, a consumption block, or a fee from the published schedule.</param>
/// <param name="Description">What the line says.</param>
/// <param name="TierSequence">Which tier of the tariff produced it. Absent on a fee.</param>
/// <param name="Units">Units charged.</param>
/// <param name="RatePerUnit">Price of one unit inside the tier.</param>
/// <param name="Amount">What the line comes to.</param>
public sealed record BillLineResponse(
    int Sequence,
    string Kind,
    string Description,
    int? TierSequence,
    decimal? Units,
    decimal? RatePerUnit,
    decimal Amount)
{
    /// <summary>Projects a line for the wire.</summary>
    public static BillLineResponse From(BillLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new BillLineResponse(
            line.Sequence,
            line.Kind.ToString(),
            line.Description,
            line.TierSequence,
            line.Units,
            line.RatePerUnit,
            line.Amount);
    }
}

/// <summary>One correction to a bill, as the API returns it.</summary>
/// <param name="Id">Identifier of the adjustment.</param>
/// <param name="Sequence">Position in the bill's adjustment history, from 1.</param>
/// <param name="Kind">Whether it was money off the bill or money on to it.</param>
/// <param name="Amount">The signed change to what is owed — negative on a credit.</param>
/// <param name="AmountDueAfter">What the bill came to once it was applied.</param>
/// <param name="Reason">Why it was made.</param>
/// <param name="ActorId">Subject id of whoever made it.</param>
/// <param name="ActorName">Their name at the time.</param>
/// <param name="RecordedAt">When it was made.</param>
public sealed record BillAdjustmentResponse(
    Guid Id,
    int Sequence,
    string Kind,
    decimal Amount,
    decimal AmountDueAfter,
    string Reason,
    string ActorId,
    string? ActorName,
    DateTimeOffset RecordedAt)
{
    /// <summary>Projects an adjustment for the wire.</summary>
    public static BillAdjustmentResponse From(BillAdjustment adjustment)
    {
        ArgumentNullException.ThrowIfNull(adjustment);

        return new BillAdjustmentResponse(
            adjustment.Id,
            adjustment.Sequence,
            adjustment.Kind.ToString(),
            adjustment.Amount,
            adjustment.AmountDueAfter,
            adjustment.Reason,
            adjustment.ActorId,
            adjustment.ActorName,
            adjustment.RecordedAt);
    }
}

/// <summary>A bill as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="BillNumber">The number printed on it.</param>
/// <param name="ServiceAccountId">The account billed.</param>
/// <param name="AccountNumber">Its number.</param>
/// <param name="CustomerId">Who owes it.</param>
/// <param name="CustomerName">Their name at the time.</param>
/// <param name="ServiceLocationId">The premise supplied.</param>
/// <param name="Kind">What the bill was raised for — a period of supply, or fees alone.</param>
/// <param name="RatePlanId">The tariff version priced against. Absent on a charge bill.</param>
/// <param name="RatePlanCode">Its code. Absent on a charge bill.</param>
/// <param name="RatePlanName">Its name. Absent on a charge bill.</param>
/// <param name="RatePlanEffectiveFrom">The day that version took effect. Absent on a charge bill.</param>
/// <param name="Currency">ISO 4217 code every amount is expressed in.</param>
/// <param name="UnitOfMeasure">What the units are measured in.</param>
/// <param name="PeriodStart">First day of the billed period.</param>
/// <param name="PeriodEnd">Last day of it.</param>
/// <param name="CycleCode">The reading cycle it came from.</param>
/// <param name="MeterReadingId">The reading that closed the period.</param>
/// <param name="MeterId">The meter it came off.</param>
/// <param name="MeterNumber">Its number.</param>
/// <param name="PreviousReading">The dials at the start of the period.</param>
/// <param name="CurrentReading">The dials at the end of it.</param>
/// <param name="Consumption">Units billed.</param>
/// <param name="TotalAmount">What the bill comes to as printed. Never moves once it is calculated.</param>
/// <param name="FeeAmount">How much of that is fees from the published schedule rather than supply.</param>
/// <param name="AdjustmentTotal">The signed sum of the corrections made to it since.</param>
/// <param name="AmountDue">What is owed today — the printed total plus those corrections.</param>
/// <param name="AmountPaid">How much has been paid.</param>
/// <param name="Balance">What is still owed.</param>
/// <param name="Status">Where the bill stands.</param>
/// <param name="AllowedTransitions">The statuses it may move to, for rendering buttons.</param>
/// <param name="IsOutstanding">Whether the utility is still owed money on it.</param>
/// <param name="IssuedOn">The day it was issued.</param>
/// <param name="DueDate">The day payment falls due.</param>
/// <param name="PaidAt">When it was settled in full.</param>
/// <param name="StatusReason">Why the status last moved.</param>
/// <param name="CreatedAt">When it was calculated.</param>
/// <param name="ActorId">Subject id of whoever raised it.</param>
/// <param name="ActorName">Their name at the time.</param>
/// <param name="Lines">Its lines, in order. Empty on a list, which does not load them.</param>
/// <param name="Adjustments">Its corrections, in order. Empty on a list, for the same reason.</param>
public sealed record BillResponse(
    Guid Id,
    string BillNumber,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    string CustomerName,
    Guid ServiceLocationId,
    string Kind,
    Guid? RatePlanId,
    string? RatePlanCode,
    string? RatePlanName,
    DateOnly? RatePlanEffectiveFrom,
    string Currency,
    string? UnitOfMeasure,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string? CycleCode,
    Guid? MeterReadingId,
    Guid? MeterId,
    string? MeterNumber,
    decimal? PreviousReading,
    decimal? CurrentReading,
    decimal Consumption,
    decimal TotalAmount,
    decimal FeeAmount,
    decimal AdjustmentTotal,
    decimal AmountDue,
    decimal AmountPaid,
    decimal Balance,
    string Status,
    IReadOnlyList<string> AllowedTransitions,
    bool IsOutstanding,
    DateOnly? IssuedOn,
    DateOnly? DueDate,
    DateTimeOffset? PaidAt,
    string? StatusReason,
    DateTimeOffset CreatedAt,
    string ActorId,
    string? ActorName,
    IReadOnlyList<BillLineResponse> Lines,
    IReadOnlyList<BillAdjustmentResponse> Adjustments)
{
    /// <summary>Projects a bill for the wire.</summary>
    public static BillResponse From(Bill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);

        return new BillResponse(
            bill.Id,
            bill.BillNumber,
            bill.ServiceAccountId,
            bill.AccountNumber,
            bill.CustomerId,
            bill.CustomerName,
            bill.ServiceLocationId,
            bill.Kind.ToString(),
            bill.RatePlanId,
            bill.RatePlanCode,
            bill.RatePlanName,
            bill.RatePlanEffectiveFrom,
            bill.Currency,
            bill.UnitOfMeasure,
            bill.PeriodStart,
            bill.PeriodEnd,
            bill.CycleCode,
            bill.MeterReadingId,
            bill.MeterId,
            bill.MeterNumber,
            bill.PreviousReading,
            bill.CurrentReading,
            bill.Consumption,
            bill.TotalAmount,
            bill.FeeAmount,
            bill.AdjustmentTotal,
            bill.AmountDue,
            bill.AmountPaid,
            bill.Balance,
            bill.Status.ToString(),

            // By name, so a UI renders buttons from what the state machine actually allows rather
            // than from a list it keeps in step by hand (WP-1.5's shape).
            [.. bill.AllowedTransitions.Select(status => status.ToString())],
            bill.IsOutstanding,
            bill.IssuedOn,
            bill.DueDate,
            bill.PaidAt,
            bill.StatusReason,
            bill.CreatedAt,
            bill.ActorId,
            bill.ActorName,
            [.. bill.Lines.OrderBy(line => line.Sequence).Select(BillLineResponse.From)],
            [.. bill.Adjustments.OrderBy(adjustment => adjustment.Sequence).Select(BillAdjustmentResponse.From)]);
    }
}

/// <summary>A reading a billing run did not bill, as the API returns it.</summary>
/// <param name="MeterReadingId">The reading that was not billed.</param>
/// <param name="ServiceLocationId">The premise it was taken at.</param>
/// <param name="MeterNumber">The meter that produced it.</param>
/// <param name="Reason">Why it was skipped.</param>
public sealed record SkippedReadingResponse(Guid MeterReadingId, Guid ServiceLocationId, string MeterNumber, string Reason)
{
    /// <summary>Projects a skipped reading for the wire.</summary>
    public static SkippedReadingResponse From(SkippedReading skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);

        return new SkippedReadingResponse(
            skipped.MeterReadingId,
            skipped.ServiceLocationId,
            skipped.MeterNumber,
            skipped.Reason);
    }
}

/// <summary>What a billing run produced, as the API returns it.</summary>
/// <param name="CycleCode">The reading cycle billed.</param>
/// <param name="Raised">How many bills were raised.</param>
/// <param name="TotalBilled">What they come to.</param>
/// <param name="SkippedCount">How many readings were passed over.</param>
/// <param name="ByReason">How many were passed over for each reason.</param>
/// <param name="Bills">Every bill raised, as a draft.</param>
/// <param name="Skipped">Every reading that was not billed, and why.</param>
public sealed record BillingRunResponse(
    string CycleCode,
    int Raised,
    decimal TotalBilled,
    int SkippedCount,
    IReadOnlyDictionary<string, int> ByReason,
    IReadOnlyList<BillResponse> Bills,
    IReadOnlyList<SkippedReadingResponse> Skipped)
{
    /// <summary>Projects a run result for the wire.</summary>
    public static BillingRunResponse From(BillingRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return new BillingRunResponse(
            run.CycleCode,
            run.Raised,
            run.TotalBilled,
            run.SkippedCount,
            run.ByReason,
            [.. run.Bills.Select(BillResponse.From)],
            [.. run.Skipped.Select(SkippedReadingResponse.From)]);
    }
}

/// <summary>What an overdue review found, as the API returns it.</summary>
/// <param name="AsOf">The day judged against.</param>
/// <param name="MarkedOverdue">How many bills moved.</param>
/// <param name="TotalOverdue">What is now overdue.</param>
/// <param name="Bills">The bills that moved.</param>
public sealed record OverdueReviewResponse(
    DateOnly AsOf,
    int MarkedOverdue,
    decimal TotalOverdue,
    IReadOnlyList<BillResponse> Bills)
{
    /// <summary>Projects a review result for the wire.</summary>
    public static OverdueReviewResponse From(OverdueReviewResult review)
    {
        ArgumentNullException.ThrowIfNull(review);

        return new OverdueReviewResponse(
            review.AsOf,
            review.MarkedOverdue,
            review.TotalOverdue,
            [.. review.Bills.Select(BillResponse.From)]);
    }
}

/// <summary>The billing register's HTTP surface.</summary>
public static class BillEndpoints
{
    /// <summary>Route prefix of the billing register.</summary>
    public const string RoutePrefix = "/api/bills";

    /// <summary>Default page size for a bill list.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Maps the billing endpoints.</summary>
    public static IEndpointRouteBuilder MapBillEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var bills = endpoints.MapGroup(RoutePrefix).WithTags("Billing");

        // The register-wide list, and with ?outstandingOnly=true the AR worklist. Filtered on the
        // server; there is no sort and no total, the same shape every GridCore registry list has
        // (WP-1.5). Lines are not loaded — a page of fifty bills does not want two hundred lines —
        // and adjustments only when ?includeAdjustments=true asks for them.
        bills
            .MapGet("/", async (
                    Guid? serviceAccountId,
                    Guid? customerId,
                    BillStatus? status,
                    bool? outstandingOnly,
                    string? cycleCode,
                    int? limit,
                    bool? includeAdjustments,
                    [FromServices] IBillService register,
                    CancellationToken cancellationToken) =>
                Results.Ok((await register.ListAsync(
                        new BillQuery(
                            serviceAccountId,
                            customerId,
                            status,
                            outstandingOnly,
                            cycleCode,
                            limit ?? DefaultPageSize,
                            includeAdjustments ?? false),
                        cancellationToken))
                    .Select(BillResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Billing.Read)
            .WithName("ListBills");

        bills
            .MapGet("/{id:guid}", async (
                    [FromRoute] Guid id,
                    [FromServices] IBillService register,
                    CancellationToken cancellationToken) =>
                await register.FindAsync(id, cancellationToken) is { } bill
                    ? Results.Ok(BillResponse.From(bill))
                    : BillingProblems.BillNotFound(id))
            .RequirePermission(Permissions.Billing.Read)
            .WithName("GetBill");

        // A billing run is a POST sub-resource, never a GET: it writes a batch of bills and an audit
        // entry. It produces DRAFTS and publishes nothing — issuing is the separate act that makes a
        // bill money the utility is owed, and the exception worklist is worked in between.
        bills
            .MapPost("/runs", (
                    RunBillingRequest body,
                    [FromServices] IBillService register,
                    CancellationToken cancellationToken) =>
                BillingProblems.RunAsync(async () =>
                    Results.Ok(BillingRunResponse.From(await register.RunAsync(
                        new RunBillingInput(body.CycleCode),
                        cancellationToken)))))
            .RequirePermission(Permissions.Billing.Generate)
            .WithValidation<RunBillingRequest>()
            .WithName("RunBilling");

        bills
            .MapPost("/{id:guid}/issue", (
                    [FromRoute] Guid id,
                    IssueBillRequest body,
                    [FromServices] IBillService register,
                    CancellationToken cancellationToken) =>
                BillingProblems.RunAsync(async () =>
                    Results.Ok(BillResponse.From(await register.IssueAsync(
                        id,
                        new IssueBillInput(body.IssuedOn, body.DueDate, body.Reason),
                        cancellationToken)))))
            .RequirePermission(Permissions.Billing.Generate)
            .WithValidation<IssueBillRequest>()
            .WithName("IssueBill");

        bills
            .MapPost("/{id:guid}/cancel", (
                    [FromRoute] Guid id,
                    CancelBillRequest body,
                    [FromServices] IBillService register,
                    CancellationToken cancellationToken) =>
                BillingProblems.RunAsync(async () =>
                    Results.Ok(BillResponse.From(await register.CancelAsync(
                        id,
                        new CancelBillInput(body.Reason),
                        cancellationToken)))))
            .RequirePermission(Permissions.Billing.Generate)
            .WithValidation<CancelBillRequest>()
            .WithName("CancelBill");

        // THE SENSITIVE ONE. A sub-resource rather than /adjust, because an adjustment is a thing
        // the bill now has and not merely something done to it — it is the only write in this module
        // that creates a row somebody will later read on its own.
        //
        // Gated on billing.adjust, which is a genuinely different gate rather than a tidier name for
        // the same one: WP-0.3 gave Managers billing.adjust WITHOUT billing.generate, so the caller
        // who may credit a disputed bill is not the caller who may raise one, and vice versa is
        // false too. The endpoint test asserts the two policies differ.
        bills
            .MapPost("/{id:guid}/adjustments", (
                    [FromRoute] Guid id,
                    AdjustBillRequest body,
                    [FromServices] IBillService register,
                    CancellationToken cancellationToken) =>
                BillingProblems.RunAsync(async () =>
                    Results.Ok(BillResponse.From(await register.AdjustAsync(
                        id,
                        new AdjustBillInput(body.Kind, body.Amount, body.Reason),
                        cancellationToken)))))
            .RequirePermission(Permissions.Billing.Adjust)
            .WithValidation<AdjustBillRequest>()
            .WithName("AdjustBill");

        // The button that makes Overdue reachable without a scheduler. WP-0.4's scheduler exists and
        // nothing is registered on it yet; a job that quietly moved bills overnight is a real feature
        // with a real configuration screen behind it, and half of one here would be worse than a
        // review somebody runs and can see the result of.
        bills
            .MapPost("/overdue-review", (
                    OverdueReviewRequest body,
                    [FromServices] IBillService register,
                    CancellationToken cancellationToken) =>
                BillingProblems.RunAsync(async () =>
                    Results.Ok(OverdueReviewResponse.From(await register.ReviewOverdueAsync(
                        new OverdueReviewInput(body.AsOf),
                        cancellationToken)))))
            .RequirePermission(Permissions.Billing.Generate)
            .WithValidation<OverdueReviewRequest>()
            .WithName("ReviewOverdueBills");

        return endpoints;
    }
}
