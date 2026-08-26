using FluentValidation;
using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.Delinquency;

/// <summary>Body of a request to record that a dunning notice was served.</summary>
/// <param name="NoticeType">Which notice went out.</param>
/// <param name="ServedOn">The day it went out. Today when the caller does not say.</param>
/// <param name="Notes">What the desk wants to add.</param>
public sealed record ServeNoticeRequest(DunningNoticeType NoticeType, DateOnly? ServedOn = null, string? Notes = null);

/// <summary>Body of a request to evaluate an account for disconnection.</summary>
/// <param name="AsOf">The day to judge against. Today when the caller does not say.</param>
public sealed record EvaluateDisconnectionRequest(DateOnly? AsOf = null);

/// <summary>One aged band of an arrears picture, as the API returns it.</summary>
/// <param name="Label">What the band is called.</param>
/// <param name="FromDays">The fewest days past due in it.</param>
/// <param name="ToDays">The most, or null on the open-ended oldest band.</param>
/// <param name="Amount">What is owed in it.</param>
public sealed record ArrearsBucketResponse(string Label, int FromDays, int? ToDays, decimal Amount);

/// <summary>One outstanding bill behind the arrears, as the API returns it.</summary>
/// <param name="Id">Identifier of the bill.</param>
/// <param name="BillNumber">Its number, as printed.</param>
/// <param name="DueDate">The day it fell due.</param>
/// <param name="Balance">What is still owed on it.</param>
/// <param name="DaysPastDue">How late it is. Zero where it is not yet due.</param>
/// <param name="IsPastDue">Whether the due date has passed.</param>
public sealed record ArrearsBillResponse(
    Guid Id,
    string BillNumber,
    DateOnly? DueDate,
    decimal Balance,
    int DaysPastDue,
    bool IsPastDue);

/// <summary>What an account owes, aged, as the API returns it.</summary>
/// <param name="Currency">ISO 4217 code every amount is expressed in.</param>
/// <param name="AsOf">The day the picture was taken.</param>
/// <param name="OutstandingAmount">Everything still owed, due or not.</param>
/// <param name="PastDueAmount">The part whose due date has passed.</param>
/// <param name="CurrentAmount">The rest.</param>
/// <param name="OldestDueDate">The due date of the oldest past-due bill.</param>
/// <param name="DaysPastDue">How late that bill is.</param>
/// <param name="IsInArrears">Whether the customer is late with anything at all.</param>
/// <param name="Buckets">The ageing, oldest band last.</param>
/// <param name="Bills">The outstanding bills, oldest due date first.</param>
public sealed record AccountArrearsResponse(
    string Currency,
    DateOnly AsOf,
    decimal OutstandingAmount,
    decimal PastDueAmount,
    decimal CurrentAmount,
    DateOnly? OldestDueDate,
    int DaysPastDue,
    bool IsInArrears,
    IReadOnlyList<ArrearsBucketResponse> Buckets,
    IReadOnlyList<ArrearsBillResponse> Bills)
{
    /// <summary>Projects an arrears picture for the wire.</summary>
    public static AccountArrearsResponse From(AccountArrears arrears)
    {
        ArgumentNullException.ThrowIfNull(arrears);

        return new AccountArrearsResponse(
            arrears.Currency,
            arrears.AsOf,
            arrears.OutstandingAmount,
            arrears.PastDueAmount,
            arrears.CurrentAmount,
            arrears.OldestDueDate,
            arrears.DaysPastDue,
            arrears.IsInArrears,
            [.. arrears.Buckets.Select(bucket => new ArrearsBucketResponse(bucket.Label, bucket.FromDays, bucket.ToDays, bucket.Amount))],
            [
                .. arrears.Bills.Select(bill => new ArrearsBillResponse(
                    bill.Id,
                    bill.BillNumber,
                    bill.DueDate,
                    bill.Balance,
                    bill.DaysPastDue,
                    bill.IsPastDue)),
            ]);
    }
}

/// <summary>One published dunning step, as the API returns it.</summary>
/// <param name="NoticeType">Which notice.</param>
/// <param name="Sequence">Where it sits in the sequence.</param>
/// <param name="DaysPastDue">How far past due it falls due.</param>
/// <param name="MinimumArrears">The least that has to be owed for it to be served.</param>
/// <param name="WaitingPeriodDays">Days that must pass after it is served. Zero where it starts no clock.</param>
/// <param name="Currency">ISO 4217 code the minimum is expressed in.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Message">What it says.</param>
public sealed record DunningStepResponse(
    string NoticeType,
    int Sequence,
    int DaysPastDue,
    decimal MinimumArrears,
    int WaitingPeriodDays,
    string Currency,
    string Name,
    string Message)
{
    /// <summary>Projects a step for the wire.</summary>
    public static DunningStepResponse From(DunningStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return new DunningStepResponse(
            step.NoticeType.ToString(),
            step.Sequence,
            step.DaysPastDue,
            step.MinimumArrears,
            step.WaitingPeriodDays,
            step.Currency,
            step.Name,
            step.Message);
    }
}

/// <summary>One served notice, as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="ServiceAccountId">The account served over.</param>
/// <param name="AccountNumber">Its number.</param>
/// <param name="CustomerId">The customer served.</param>
/// <param name="CustomerName">Their name at the time.</param>
/// <param name="NoticeType">Which notice.</param>
/// <param name="ServedOn">The day it went out.</param>
/// <param name="ArrearsAmount">What was past due then.</param>
/// <param name="Currency">ISO 4217 code that figure is in.</param>
/// <param name="DaysPastDue">How late the oldest past-due bill was.</param>
/// <param name="WaitingPeriodDays">The period it started.</param>
/// <param name="EffectiveFrom">The first day the act it warns of may be taken.</param>
/// <param name="Notes">What the desk wrote beside it.</param>
/// <param name="ActorId">Subject id of whoever served it.</param>
/// <param name="ActorName">Their name at the time.</param>
/// <param name="RecordedAt">When it was recorded.</param>
public sealed record DunningNoticeResponse(
    Guid Id,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    string CustomerName,
    string NoticeType,
    DateOnly ServedOn,
    decimal ArrearsAmount,
    string Currency,
    int DaysPastDue,
    int WaitingPeriodDays,
    DateOnly? EffectiveFrom,
    string? Notes,
    string ActorId,
    string? ActorName,
    DateTimeOffset RecordedAt)
{
    /// <summary>Projects a notice for the wire.</summary>
    public static DunningNoticeResponse From(DunningNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        return new DunningNoticeResponse(
            notice.Id,
            notice.ServiceAccountId,
            notice.AccountNumber,
            notice.CustomerId,
            notice.CustomerName,
            notice.NoticeType.ToString(),
            notice.ServedOn,
            notice.ArrearsAmount,
            notice.Currency,
            notice.DaysPastDue,
            notice.WaitingPeriodDays,
            notice.EffectiveFrom,
            notice.Notes,
            notice.ActorId,
            notice.ActorName,
            notice.RecordedAt);
    }
}

/// <summary>One eligibility test, as the API returns it.</summary>
/// <param name="Name">What the test is.</param>
/// <param name="IsSatisfied">Whether the account passes it.</param>
/// <param name="Detail">The figures or dates behind the answer.</param>
public sealed record EligibilityTestResponse(string Name, bool IsSatisfied, string Detail);

/// <summary>Where an account stands against the four disconnection tests, as the API returns it.</summary>
/// <param name="ServiceAccountId">The account judged.</param>
/// <param name="AsOf">The day judged against.</param>
/// <param name="Currency">ISO 4217 code every figure is in.</param>
/// <param name="ArrearsBeforeOffset">What was past due before the deposit was set against it.</param>
/// <param name="DepositHeldBeforeOffset">What the utility holds.</param>
/// <param name="OffsetAmount">What qualifies against the arrears.</param>
/// <param name="ArrearsAfterOffset">What remains past due once it has been applied.</param>
/// <param name="DepositHeldAfterOffset">What is left on deposit.</param>
/// <param name="Threshold">The published arrears the disconnection step asks for.</param>
/// <param name="DisconnectionNoticeServedOn">The day the disconnection notice went out.</param>
/// <param name="WaitingPeriodDays">The published waiting period.</param>
/// <param name="EligibleFrom">The first day disconnection could be taken on the notice served.</param>
/// <param name="ArrangementStatus">The payment arrangement protecting the account, where there is one.</param>
/// <param name="IsEligible">Whether the supply may be cut off.</param>
/// <param name="DepositClearsArrears">Whether the deposit clears the whole of the arrears.</param>
/// <param name="IsOffsetApplied">Whether the deposit movement described has actually been made.</param>
/// <param name="Tests">Every test, in the order a rep reads them.</param>
/// <param name="Blockers">What stands in the way.</param>
public sealed record DisconnectionEligibilityResponse(
    Guid ServiceAccountId,
    DateOnly AsOf,
    string Currency,
    decimal ArrearsBeforeOffset,
    decimal DepositHeldBeforeOffset,
    decimal OffsetAmount,
    decimal ArrearsAfterOffset,
    decimal DepositHeldAfterOffset,
    decimal Threshold,
    DateOnly? DisconnectionNoticeServedOn,
    int WaitingPeriodDays,
    DateOnly? EligibleFrom,
    string? ArrangementStatus,
    bool IsEligible,
    bool DepositClearsArrears,
    bool IsOffsetApplied,
    IReadOnlyList<EligibilityTestResponse> Tests,
    IReadOnlyList<string> Blockers)
{
    /// <summary>Projects an eligibility for the wire.</summary>
    public static DisconnectionEligibilityResponse From(DisconnectionEligibility eligibility)
    {
        ArgumentNullException.ThrowIfNull(eligibility);

        return new DisconnectionEligibilityResponse(
            eligibility.ServiceAccountId,
            eligibility.AsOf,
            eligibility.Currency,
            eligibility.ArrearsBeforeOffset,
            eligibility.DepositHeldBeforeOffset,
            eligibility.OffsetAmount,
            eligibility.ArrearsAfterOffset,
            eligibility.DepositHeldAfterOffset,
            eligibility.Threshold,
            eligibility.DisconnectionNoticeServedOn,
            eligibility.WaitingPeriodDays,
            eligibility.EligibleFrom,
            eligibility.Arrangement?.Status,
            eligibility.IsEligible,
            eligibility.DepositClearsArrears,
            eligibility.IsOffsetApplied,
            [.. eligibility.Tests.Select(test => new EligibilityTestResponse(test.Name, test.IsSatisfied, test.Detail))],
            eligibility.Blockers);
    }
}

/// <summary>One account's delinquency picture, as the API returns it.</summary>
/// <param name="ServiceAccountId">The account.</param>
/// <param name="AccountNumber">Its number.</param>
/// <param name="CustomerId">Who holds it.</param>
/// <param name="CustomerName">Their name.</param>
/// <param name="AccountStatus">Where the account stands.</param>
/// <param name="Arrears">What is owed, aged.</param>
/// <param name="DepositHeld">What the utility holds against the customer.</param>
/// <param name="Steps">The published dunning sequence.</param>
/// <param name="DueStep">The furthest step this account has reached.</param>
/// <param name="Notices">Every notice served, newest first.</param>
/// <param name="Eligibility">Where it stands against the four tests, with the offset computed and not made.</param>
public sealed record DelinquencyResponse(
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    string CustomerName,
    string AccountStatus,
    AccountArrearsResponse Arrears,
    decimal DepositHeld,
    IReadOnlyList<DunningStepResponse> Steps,
    DunningStepResponse? DueStep,
    IReadOnlyList<DunningNoticeResponse> Notices,
    DisconnectionEligibilityResponse Eligibility)
{
    /// <summary>Projects a picture for the wire.</summary>
    public static DelinquencyResponse From(DelinquencyPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);

        return new DelinquencyResponse(
            picture.ServiceAccountId,
            picture.AccountNumber,
            picture.CustomerId,
            picture.CustomerName,
            picture.AccountStatus,
            AccountArrearsResponse.From(picture.Arrears),
            picture.DepositHeld,
            [.. picture.Steps.Select(DunningStepResponse.From)],
            picture.DueStep is null ? null : DunningStepResponse.From(picture.DueStep),
            [.. picture.Notices.Select(DunningNoticeResponse.From)],
            DisconnectionEligibilityResponse.From(picture.Eligibility));
    }
}

/// <summary>What an evaluation did and decided, as the API returns it.</summary>
/// <param name="Eligibility">The answer, with the offset now actually made.</param>
/// <param name="OffsetAmount">What the offset came to.</param>
/// <param name="OffsetEntries">The deposit movements it wrote.</param>
public sealed record DisconnectionEvaluationResponse(
    DisconnectionEligibilityResponse Eligibility,
    decimal OffsetAmount,
    IReadOnlyList<DepositEntryResponse> OffsetEntries)
{
    /// <summary>Projects an evaluation for the wire.</summary>
    public static DisconnectionEvaluationResponse From(DisconnectionEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        return new DisconnectionEvaluationResponse(
            DisconnectionEligibilityResponse.From(evaluation.Eligibility),
            evaluation.OffsetAmount,
            [.. evaluation.OffsetEntries.Select(DepositEntryResponse.From)]);
    }
}

/// <summary>Rules for serving a dunning notice.</summary>
public sealed class ServeNoticeRequestValidator : AbstractValidator<ServeNoticeRequest>
{
    /// <summary>Builds the rules.</summary>
    public ServeNoticeRequestValidator()
    {
        // A body naming a notice that is not one of ours reads as a 400 about the field the caller
        // got wrong, rather than reaching the sequence and being refused there after a query.
        RuleFor(request => request.NoticeType).IsInEnum();

        RuleFor(request => request.Notes!).MaximumLength(DunningNotice.NotesLength);
    }
}

/// <summary>
/// The delinquency register's HTTP surface (WP-2.19).
/// </summary>
/// <remarks>
/// <para>
/// <b>Hung off the service account, unlike WP-2.18's applications.</b> An application is worked from
/// a queue across every customer, so it earned a resource of its own; delinquency is always about
/// one supply at one premise — the arrears are the account's, the notices are served over it and the
/// disconnection would be of it — and a route that made the account a filter would name it after the
/// one thing every question here has in common.
/// </para>
/// <para>
/// <b>Two gates, and the split is the package's central design.</b> The picture and the notices are
/// <see cref="Permissions.Customers.Read"/>: quoting arrears and reading what has been served is
/// what a rep does all day. Serving a notice is <see cref="Permissions.Customers.Write"/> —
/// clerical, the same grant an intake takes. The evaluation is
/// <see cref="Permissions.Customers.Deposit"/> on the route <i>and</i> in the service, because it
/// sets a customer's deposit against what they owe: it is a deposit movement wearing a decision's
/// clothes, and gating it on anything else would be a way of spending a deposit without holding the
/// permission to spend one.
/// </para>
/// </remarks>
public static class DelinquencyEndpoints
{
    /// <summary>Route prefix of the delinquency surface, under the service account it is about.</summary>
    public const string RoutePrefix = "/api/service-accounts/{serviceAccountId:guid}";

    /// <summary>Maps the delinquency endpoints.</summary>
    public static IEndpointRouteBuilder MapDelinquencyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Delinquency");

        group
            .MapGet("/delinquency", (
                    [FromRoute] Guid serviceAccountId,
                    DateOnly? asOf,
                    [FromServices] IDelinquencyService delinquency,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(DelinquencyResponse.From(
                        await delinquency.GetAsync(serviceAccountId, asOf, cancellationToken)))))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("GetAccountDelinquency");

        group
            .MapGet("/dunning-notices", async (
                    [FromRoute] Guid serviceAccountId,
                    int? limit,
                    [FromServices] IDelinquencyService delinquency,
                    CancellationToken cancellationToken) =>
                Results.Ok((await delinquency.ListNoticesAsync(
                        serviceAccountId,
                        limit ?? DelinquencyService.MaxNotices,
                        cancellationToken))
                    .Select(DunningNoticeResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("ListAccountDunningNotices");

        group
            .MapPost("/dunning-notices", (
                    [FromRoute] Guid serviceAccountId,
                    ServeNoticeRequest body,
                    [FromServices] IDelinquencyService delinquency,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var notice = await delinquency.ServeAsync(
                        serviceAccountId,
                        new ServeNoticeInput(body.NoticeType, body.ServedOn, body.Notes),
                        cancellationToken);

                    return Results.Created(
                        $"/api/service-accounts/{serviceAccountId}/dunning-notices/{notice.Id}",
                        DunningNoticeResponse.From(notice));
                }))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<ServeNoticeRequest>()
            .WithName("ServeDunningNotice");

        // THE STATUTORY ONE. A POST, because evaluating eligibility applies the held deposit to
        // qualifying past-due amounts — see IDelinquencyService.EvaluateAsync. A GET that moved money
        // would move it again on every refresh.
        group
            .MapPost("/disconnection-eligibility", (
                    [FromRoute] Guid serviceAccountId,
                    EvaluateDisconnectionRequest? body,
                    [FromServices] IDelinquencyService delinquency,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(DisconnectionEvaluationResponse.From(await delinquency.EvaluateAsync(
                        serviceAccountId,
                        new EvaluateDisconnectionInput(body?.AsOf),
                        cancellationToken)))))
            .RequirePermission(Permissions.Customers.Deposit)
            .WithName("EvaluateDisconnectionEligibility");

        return endpoints;
    }
}
