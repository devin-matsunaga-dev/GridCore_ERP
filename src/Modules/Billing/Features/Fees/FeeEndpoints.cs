using FluentValidation;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Billing.Features.Fees;

/// <summary>Body of a request to raise a fee against a service account.</summary>
/// <param name="ServiceAccountId">The account to charge.</param>
/// <param name="Code">Which published fee.</param>
/// <param name="Reason">Why. Required — this is the sensitive action invariant 5 is about.</param>
/// <param name="RaisedOn">The day to price against. Defaults to today.</param>
public sealed record RaiseChargeRequest(Guid ServiceAccountId, FeeCode Code, string Reason, DateOnly? RaisedOn = null);

/// <summary>Body of a request to withdraw a raised charge.</summary>
/// <param name="Reason">Why. Required — it removes money the utility was going to be owed.</param>
public sealed record CancelChargeRequest(string Reason);

/// <summary>Body of a request to bill a charge at the counter.</summary>
/// <param name="Reason">What to record against the bill's issue.</param>
public sealed record BillChargeRequest(string? Reason = null);

/// <summary>One published fee as the API returns it, priced for the day asked about.</summary>
/// <param name="Code">Which published fee.</param>
/// <param name="Name">What the line says when it reaches a bill.</param>
/// <param name="Description">What it covers and where the figure came from.</param>
/// <param name="ServiceType">The service it is published against.</param>
/// <param name="Amount">What it costs on that day.</param>
/// <param name="Currency">ISO 4217 code the amount is expressed in.</param>
/// <param name="EffectiveFrom">The day that figure took effect.</param>
/// <param name="FeeScheduleId">Which schedule row answered — what a raised charge stamps.</param>
public sealed record FeeScheduleResponse(
    string Code,
    string Name,
    string Description,
    string ServiceType,
    decimal Amount,
    string Currency,
    DateOnly EffectiveFrom,
    Guid FeeScheduleId)
{
    /// <summary>Projects an assessment for the wire.</summary>
    public static FeeScheduleResponse From(FeeAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        return new FeeScheduleResponse(
            assessment.Code.ToString(),
            assessment.Name,
            assessment.Description,
            assessment.ServiceType.ToString(),
            assessment.Amount,
            assessment.Currency,
            assessment.EffectiveFrom,
            assessment.FeeScheduleId);
    }
}

/// <summary>A raised charge as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="ServiceAccountId">The account charged.</param>
/// <param name="AccountNumber">Its number.</param>
/// <param name="CustomerId">Who will owe it.</param>
/// <param name="CustomerName">Their name at the time it was raised.</param>
/// <param name="Code">Which published fee.</param>
/// <param name="Description">What the line says on the bill.</param>
/// <param name="Amount">What was charged.</param>
/// <param name="Currency">ISO 4217 code it is expressed in.</param>
/// <param name="FeeScheduleId">The schedule row that priced it.</param>
/// <param name="ScheduleEffectiveFrom">The day that figure took effect.</param>
/// <param name="RaisedOn">The day priced against.</param>
/// <param name="Reason">Why it was raised.</param>
/// <param name="Status">Where the charge stands.</param>
/// <param name="AllowedTransitions">The statuses it may move to, for rendering buttons.</param>
/// <param name="IsPending">Whether it is still waiting for a bill.</param>
/// <param name="BillId">The bill it landed on.</param>
/// <param name="BillNumber">That bill's number.</param>
/// <param name="RaisedAt">When it was raised.</param>
/// <param name="StatusChangedAt">When the status last moved.</param>
/// <param name="StatusReason">Why it last moved.</param>
/// <param name="ActorId">Subject id of whoever raised it.</param>
/// <param name="ActorName">Their name at the time.</param>
public sealed record AccountChargeResponse(
    Guid Id,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    string CustomerName,
    string Code,
    string Description,
    decimal Amount,
    string Currency,
    Guid FeeScheduleId,
    DateOnly ScheduleEffectiveFrom,
    DateOnly RaisedOn,
    string Reason,
    string Status,
    IReadOnlyList<string> AllowedTransitions,
    bool IsPending,
    Guid? BillId,
    string? BillNumber,
    DateTimeOffset RaisedAt,
    DateTimeOffset StatusChangedAt,
    string? StatusReason,
    string ActorId,
    string? ActorName)
{
    /// <summary>Projects a charge for the wire.</summary>
    public static AccountChargeResponse From(AccountCharge charge)
    {
        ArgumentNullException.ThrowIfNull(charge);

        return new AccountChargeResponse(
            charge.Id,
            charge.ServiceAccountId,
            charge.AccountNumber,
            charge.CustomerId,
            charge.CustomerName,
            charge.Code.ToString(),
            charge.Description,
            charge.Amount,
            charge.Currency,
            charge.FeeScheduleId,
            charge.ScheduleEffectiveFrom,
            charge.RaisedOn,
            charge.Reason,
            charge.Status.ToString(),

            // By name, so a UI renders buttons from what the state machine actually allows rather
            // than from a list it keeps in step by hand (WP-1.5's shape).
            [.. charge.AllowedTransitions.Select(status => status.ToString())],
            charge.IsPending,
            charge.BillId,
            charge.BillNumber,
            charge.RaisedAt,
            charge.StatusChangedAt,
            charge.StatusReason,
            charge.ActorId,
            charge.ActorName);
    }
}

/// <summary>What billing a charge at the counter produced, as the API returns it.</summary>
/// <param name="Charge">The charge, now billed.</param>
/// <param name="Bill">The bill it was put on — raised and issued in the same act.</param>
public sealed record CounterBillResponse(AccountChargeResponse Charge, BillResponse Bill)
{
    /// <summary>Projects the result for the wire.</summary>
    public static CounterBillResponse From(CounterBillResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CounterBillResponse(AccountChargeResponse.From(result.Charge), BillResponse.From(result.Bill));
    }
}

/// <summary>Rules for raising a fee.</summary>
public sealed class RaiseChargeRequestValidator : AbstractValidator<RaiseChargeRequest>
{
    /// <summary>Builds the rules.</summary>
    public RaiseChargeRequestValidator()
    {
        RuleFor(request => request.ServiceAccountId).NotEmpty();

        // A body naming a fee that is not one of ours reads as a 400 about the field the caller got
        // wrong, rather than reaching the schedule and being refused there after a query.
        RuleFor(request => request.Code).IsInEnum();

        // Required here as well as in the aggregate. A fee is money the customer will be asked for,
        // and invariant 5 is the whole point of this endpoint.
        RuleFor(request => request.Reason).NotEmpty().MaximumLength(AccountCharge.ReasonLength);
    }
}

/// <summary>Rules for withdrawing a raised charge.</summary>
public sealed class CancelChargeRequestValidator : AbstractValidator<CancelChargeRequest>
{
    /// <summary>Builds the rules.</summary>
    public CancelChargeRequestValidator() =>
        RuleFor(request => request.Reason).NotEmpty().MaximumLength(AccountCharge.ReasonLength);
}

/// <summary>Rules for billing a charge at the counter.</summary>
public sealed class BillChargeRequestValidator : AbstractValidator<BillChargeRequest>
{
    /// <summary>Builds the rules.</summary>
    public BillChargeRequestValidator() =>
        RuleFor(request => request.Reason!).MaximumLength(Bill.ReasonLength);
}

/// <summary>The fee schedule's and the charge register's HTTP surface.</summary>
public static class FeeEndpoints
{
    /// <summary>Route prefix of the published fee schedule.</summary>
    public const string SchedulePrefix = "/api/fee-schedule";

    /// <summary>Route prefix of the charge register.</summary>
    public const string ChargesPrefix = "/api/account-charges";

    /// <summary>Default page size for a charge list.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Maps the fee schedule and account charge endpoints.</summary>
    public static IEndpointRouteBuilder MapFeeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var schedule = endpoints.MapGroup(SchedulePrefix).WithTags("Billing");

        // READ ONLY, and there is deliberately no write. A published fee is reference data: changing
        // $135 to $150 is a new effective-dated row in a migration, never an endpoint somebody can
        // point at a production database — the same call WP-0.8 made about the chart of accounts and
        // WP-2.8 about the deposit schedule.
        schedule
            .MapGet("/", async (
                    DateOnly? on,
                    [FromServices] IFeeScheduleService catalogue,
                    [FromServices] TimeProvider clock,
                    CancellationToken cancellationToken) =>
                Results.Ok((await catalogue.ListAsync(
                        on ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),
                        cancellationToken))
                    .Select(FeeScheduleResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Billing.Read)
            .WithName("ListFeeSchedule");

        var charges = endpoints.MapGroup(ChargesPrefix).WithTags("Billing");

        charges
            .MapGet("/", async (
                    Guid? serviceAccountId,
                    Guid? customerId,
                    AccountChargeStatus? status,
                    bool? pendingOnly,
                    int? limit,
                    [FromServices] IAccountChargeService register,
                    CancellationToken cancellationToken) =>
                Results.Ok((await register.ListAsync(
                        new AccountChargeQuery(
                            serviceAccountId,
                            customerId,
                            status,
                            pendingOnly,
                            limit ?? DefaultPageSize),
                        cancellationToken))
                    .Select(AccountChargeResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Billing.Read)
            .WithName("ListAccountCharges");

        charges
            .MapGet("/{id:guid}", async (
                    [FromRoute] Guid id,
                    [FromServices] IAccountChargeService register,
                    CancellationToken cancellationToken) =>
                await register.FindAsync(id, cancellationToken) is { } charge
                    ? Results.Ok(AccountChargeResponse.From(charge))
                    : BillingProblems.AccountChargeNotFound(id))
            .RequirePermission(Permissions.Billing.Read)
            .WithName("GetAccountCharge");

        // THE SENSITIVE ONE. Gated on billing.charge, which is a genuinely different gate from
        // billing.generate rather than a tidier name for it: customer service holds this and does
        // not hold generate, because raising one reconnection fee is not running the billing cycle
        // over every metered premise on the island. The endpoint test asserts the policies differ.
        charges
            .MapPost("/", (
                    RaiseChargeRequest body,
                    [FromServices] IAccountChargeService register,
                    CancellationToken cancellationToken) =>
                BillingProblems.RunAsync(async () =>
                {
                    var charge = await register.RaiseAsync(
                        new RaiseChargeInput(body.ServiceAccountId, body.Code, body.Reason, body.RaisedOn),
                        cancellationToken);

                    // 201 with the row: a charge is a thing that now exists and has an id somebody
                    // will bill or withdraw, unlike an adjustment, which is only readable as part of
                    // the bill it changed.
                    return Results.Created($"{ChargesPrefix}/{charge.Id}", AccountChargeResponse.From(charge));
                }))
            .RequirePermission(Permissions.Billing.Charge)
            .WithValidation<RaiseChargeRequest>()
            .WithName("RaiseAccountCharge");

        charges
            .MapPost("/{id:guid}/cancel", (
                    [FromRoute] Guid id,
                    CancelChargeRequest body,
                    [FromServices] IAccountChargeService register,
                    CancellationToken cancellationToken) =>
                BillingProblems.RunAsync(async () =>
                    Results.Ok(AccountChargeResponse.From(await register.CancelAsync(
                        id,
                        new CancelChargeInput(body.Reason),
                        cancellationToken)))))
            .RequirePermission(Permissions.Billing.Charge)
            .WithValidation<CancelChargeRequest>()
            .WithName("CancelAccountCharge");

        // The counter: a bill of its own, issued in the same act so the customer can pay it now.
        // Gated on billing.charge and not on billing.generate, because the desk that raised the fee
        // is the desk that has the customer in front of it — see AccountChargeService.BillNowAsync.
        charges
            .MapPost("/{id:guid}/bill", (
                    [FromRoute] Guid id,
                    BillChargeRequest body,
                    [FromServices] IAccountChargeService register,
                    CancellationToken cancellationToken) =>
                BillingProblems.RunAsync(async () =>
                    Results.Ok(CounterBillResponse.From(await register.BillNowAsync(
                        id,
                        new BillChargeInput(body.Reason),
                        cancellationToken)))))
            .RequirePermission(Permissions.Billing.Charge)
            .WithValidation<BillChargeRequest>()
            .WithName("BillAccountChargeNow");

        return endpoints;
    }
}
