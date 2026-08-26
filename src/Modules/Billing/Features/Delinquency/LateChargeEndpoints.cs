using FluentValidation;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Billing.Features.Delinquency;

/// <summary>Body of a request to run the late charges.</summary>
/// <param name="AsOf">The day to judge against. Today when the caller does not say.</param>
/// <param name="ServiceAccountId">One account only, where a rep is putting one right.</param>
public sealed record LateChargeRunRequest(DateOnly? AsOf = null, Guid? ServiceAccountId = null);

/// <summary>One late charge assessment as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="BillId">The bill that was late.</param>
/// <param name="BillNumber">Its number, as printed.</param>
/// <param name="ServiceAccountId">The account it was billed to.</param>
/// <param name="AccountNumber">Its number.</param>
/// <param name="CustomerId">Who owed it.</param>
/// <param name="PeriodStart">The first day of the month charged for.</param>
/// <param name="AssessedOn">The day judged against.</param>
/// <param name="DaysPastDue">How late the bill was on that day.</param>
/// <param name="BasisAmount">What was past due — the balance, never the printed total.</param>
/// <param name="Rate">The published rate it was taken at, as a fraction.</param>
/// <param name="Amount">What the two came to.</param>
/// <param name="Currency">ISO 4217 code the figures are expressed in.</param>
/// <param name="FeeScheduleId">The schedule row that published the rate.</param>
/// <param name="AccountChargeId">The charge it raised.</param>
/// <param name="AssessedAt">When the run ran.</param>
/// <param name="ActorId">Subject id of whoever ran it.</param>
/// <param name="ActorName">Their name at the time.</param>
public sealed record LateChargeAssessmentResponse(
    Guid Id,
    Guid BillId,
    string BillNumber,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    DateOnly PeriodStart,
    DateOnly AssessedOn,
    int DaysPastDue,
    decimal BasisAmount,
    decimal Rate,
    decimal Amount,
    string Currency,
    Guid FeeScheduleId,
    Guid AccountChargeId,
    DateTimeOffset AssessedAt,
    string ActorId,
    string? ActorName)
{
    /// <summary>Projects an assessment for the wire.</summary>
    public static LateChargeAssessmentResponse From(LateChargeAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        return new LateChargeAssessmentResponse(
            assessment.Id,
            assessment.BillId,
            assessment.BillNumber,
            assessment.ServiceAccountId,
            assessment.AccountNumber,
            assessment.CustomerId,
            assessment.PeriodStart,
            assessment.AssessedOn,
            assessment.DaysPastDue,
            assessment.BasisAmount,
            assessment.Rate,
            assessment.Amount,
            assessment.Currency,
            assessment.FeeScheduleId,
            assessment.AccountChargeId,
            assessment.AssessedAt,
            assessment.ActorId,
            assessment.ActorName);
    }
}

/// <summary>What a run did, as the API returns it.</summary>
/// <param name="AsOf">The day it judged against.</param>
/// <param name="PeriodStart">The month it charged for.</param>
/// <param name="ChargedCount">How many bills were charged.</param>
/// <param name="TotalCharged">What that came to.</param>
/// <param name="Assessed">Every assessment it wrote.</param>
/// <param name="Skipped">Every past-due bill it passed over, with the reason.</param>
public sealed record LateChargeRunResponse(
    DateOnly AsOf,
    DateOnly PeriodStart,
    int ChargedCount,
    decimal TotalCharged,
    IReadOnlyList<LateChargeAssessmentResponse> Assessed,
    IReadOnlyList<LateChargeSkipResponse> Skipped)
{
    /// <summary>Projects a run for the wire.</summary>
    public static LateChargeRunResponse From(LateChargeRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new LateChargeRunResponse(
            result.AsOf,
            result.PeriodStart,
            result.ChargedCount,
            result.TotalCharged,
            [.. result.Assessed.Select(LateChargeAssessmentResponse.From)],
            [.. result.Skipped.Select(LateChargeSkipResponse.From)]);
    }
}

/// <summary>One bill the run passed over, as the API returns it.</summary>
/// <param name="BillId">The bill.</param>
/// <param name="BillNumber">Its number.</param>
/// <param name="Reason">Why it was passed over.</param>
public sealed record LateChargeSkipResponse(Guid BillId, string BillNumber, string Reason)
{
    /// <summary>Projects a skipped bill for the wire.</summary>
    public static LateChargeSkipResponse From(LateChargeSkip skip)
    {
        ArgumentNullException.ThrowIfNull(skip);

        return new LateChargeSkipResponse(skip.BillId, skip.BillNumber, skip.Reason);
    }
}

/// <summary>Rules for running the late charges.</summary>
public sealed class LateChargeRunRequestValidator : AbstractValidator<LateChargeRunRequest>
{
    /// <summary>Builds the rules.</summary>
    public LateChargeRunRequestValidator() =>

        // An empty Guid is a caller that meant to name an account and did not; null is a caller that
        // meant the whole register. The two are different requests and only one of them is a typo.
        RuleFor(request => request.ServiceAccountId!.Value)
            .NotEmpty()
            .When(request => request.ServiceAccountId is not null);
}

/// <summary>The late-charge run's HTTP surface (WP-2.19).</summary>
/// <remarks>
/// <para>
/// <b>A run is a POST to a collection of runs, not a POST to a verb.</b> The shape the billing cycle
/// and the overdue review already take: running the late charges produces a record of having run
/// them, and the response is that record.
/// </para>
/// <para>
/// <b>Gated on <see cref="Permissions.Billing.Charge"/>, not on a grant of its own.</b> Every act
/// this endpoint performs is raising a published fee against an account, which is exactly what that
/// permission names — and a second grant covering the same act would be two grants for one job, the
/// argument <see cref="Permissions.Customers.Documents"/> already makes about the bill reprint. The
/// service demands it again, because the monthly job will reach it without passing a URL.
/// </para>
/// </remarks>
public static class LateChargeEndpoints
{
    /// <summary>Route prefix of the late-charge register.</summary>
    public const string RoutePrefix = "/api/late-charges";

    /// <summary>Default page size for the register list.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Maps the late-charge endpoints.</summary>
    public static IEndpointRouteBuilder MapLateChargeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Billing");

        group
            .MapGet("/", async (
                    [FromQuery] Guid serviceAccountId,
                    int? limit,
                    [FromServices] ILateChargeService lateCharges,
                    CancellationToken cancellationToken) =>
                Results.Ok((await lateCharges.ListAsync(
                        serviceAccountId,
                        limit ?? DefaultPageSize,
                        cancellationToken))
                    .Select(LateChargeAssessmentResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Billing.Read)
            .WithName("ListLateCharges");

        group
            .MapPost("/runs", (
                    LateChargeRunRequest? body,
                    [FromServices] ILateChargeService lateCharges,
                    CancellationToken cancellationToken) =>
                BillingProblems.RunAsync(async () =>
                    Results.Ok(LateChargeRunResponse.From(await lateCharges.RunAsync(
                        new LateChargeRunInput(body?.AsOf, body?.ServiceAccountId),
                        cancellationToken)))))
            .RequirePermission(Permissions.Billing.Charge)
            .WithValidation<LateChargeRunRequest>()
            .WithName("RunLateCharges");

        return endpoints;
    }
}
