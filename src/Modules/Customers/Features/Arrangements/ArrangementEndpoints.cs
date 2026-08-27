using FluentValidation;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.Arrangements;

/// <summary>Body of a request to propose a payment arrangement.</summary>
/// <param name="ArrearsBalance">What is being arranged. Never more than what is past due.</param>
/// <param name="DownPayment">What is taken up front. Zero where nothing is.</param>
/// <param name="InstalmentCount">How many instalments the rest is spread over.</param>
/// <param name="FirstInstalmentDue">The day the first falls due. A month out when the caller does not say.</param>
/// <param name="IntervalDays">Days between instalments after the first.</param>
/// <param name="ArrangedOn">The day it is made. Today when the caller does not say.</param>
/// <param name="Notes">What the desk wants to add.</param>
public sealed record ProposeArrangementRequest(
    decimal ArrearsBalance,
    decimal DownPayment = Money.Zero,
    int InstalmentCount = 3,
    DateOnly? FirstInstalmentDue = null,
    int IntervalDays = ArrangementSchedule.DefaultIntervalDays,
    DateOnly? ArrangedOn = null,
    string? Notes = null);

/// <summary>Body of a request to review the arrangement register.</summary>
/// <param name="AsOf">The day to judge against. Today when the caller does not say.</param>
/// <param name="ServiceAccountId">One account only, or the whole register when null.</param>
public sealed record ReviewArrangementsRequest(DateOnly? AsOf = null, Guid? ServiceAccountId = null);

/// <summary>One published arrangement ceiling, as the API returns it.</summary>
/// <param name="CustomerClass">The class it governs.</param>
/// <param name="MaximumBalance">The most a rep may arrange alone.</param>
/// <param name="Currency">ISO 4217 code that figure is in.</param>
/// <param name="MaximumInstalments">The most instalments a rep may spread it over alone.</param>
/// <param name="Notes">Where the figures came from.</param>
public sealed record ArrangementLimitResponse(
    string CustomerClass,
    decimal MaximumBalance,
    string Currency,
    int MaximumInstalments,
    string Notes)
{
    /// <summary>Projects a limit for the wire.</summary>
    public static ArrangementLimitResponse From(ArrangementLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);

        return new ArrangementLimitResponse(
            limit.CustomerClass.ToString(),
            limit.MaximumBalance,
            limit.Currency,
            limit.MaximumInstalments,
            limit.Notes);
    }
}

/// <summary>One instalment, as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="Sequence">Where it falls in the schedule.</param>
/// <param name="DueDate">The day it falls due.</param>
/// <param name="Amount">What was promised on it.</param>
/// <param name="PaidAmount">How much has arrived.</param>
/// <param name="Outstanding">What is still owed on it.</param>
/// <param name="IsSettled">Whether it has been paid in full.</param>
/// <param name="IsDownPayment">Whether it is the money taken up front.</param>
/// <param name="SettledAt">When the last payment against it landed.</param>
public sealed record ArrangementInstalmentResponse(
    Guid Id,
    int Sequence,
    DateOnly DueDate,
    decimal Amount,
    decimal PaidAmount,
    decimal Outstanding,
    bool IsSettled,
    bool IsDownPayment,
    DateTimeOffset? SettledAt)
{
    /// <summary>Projects an instalment for the wire.</summary>
    public static ArrangementInstalmentResponse From(ArrangementInstalment instalment)
    {
        ArgumentNullException.ThrowIfNull(instalment);

        return new ArrangementInstalmentResponse(
            instalment.Id,
            instalment.Sequence,
            instalment.DueDate,
            instalment.Amount,
            instalment.PaidAmount,
            instalment.Outstanding,
            instalment.IsSettled,
            instalment.IsDownPayment,
            instalment.SettledAt);
    }
}

/// <summary>One payment arrangement, as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="ArrangementNumber">Its number, as quoted.</param>
/// <param name="ServiceAccountId">The account it is against.</param>
/// <param name="AccountNumber">Its number.</param>
/// <param name="CustomerId">The customer who promised.</param>
/// <param name="CustomerName">Their name at the time.</param>
/// <param name="CustomerClass">Their class at the time.</param>
/// <param name="Status">Where it stands, as recorded.</param>
/// <param name="Standing">
/// Where it <b>effectively</b> stands today. Differs from <paramref name="Status"/> on an
/// arrangement that has missed an instalment since the last review — and it is what an account's
/// protection is read from, so it is what a screen shows.
/// </param>
/// <param name="SuppressesDisconnection">Whether it stops the supply being cut off today.</param>
/// <param name="Currency">ISO 4217 code every figure is in.</param>
/// <param name="ArrearsBalance">What was arranged.</param>
/// <param name="DownPayment">What was taken up front.</param>
/// <param name="InstalmentCount">How many instalments the rest was spread over.</param>
/// <param name="IntervalDays">Days between them.</param>
/// <param name="ScheduledAmount">What the schedule adds up to.</param>
/// <param name="PaidAmount">What has arrived.</param>
/// <param name="OutstandingAmount">What is still promised.</param>
/// <param name="ArrangedOn">The day it was made.</param>
/// <param name="ActivatedOn">The day it came into force.</param>
/// <param name="ClosedOn">The day it was kept or broken.</param>
/// <param name="LimitMaximumBalance">The ceiling that governed it.</param>
/// <param name="LimitMaximumInstalments">The instalment ceiling that governed it.</param>
/// <param name="RequiresApproval">Whether it went beyond one of them.</param>
/// <param name="ApprovalRequestId">The request raised to decide it.</param>
/// <param name="Notes">What the desk wrote beside it.</param>
/// <param name="ActorId">Subject id of whoever made it.</param>
/// <param name="ActorName">Their name at the time.</param>
/// <param name="RecordedAt">When it was recorded.</param>
/// <param name="Instalments">The schedule, in the order it falls due.</param>
public sealed record PaymentArrangementResponse(
    Guid Id,
    string ArrangementNumber,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    string CustomerName,
    string CustomerClass,
    string Status,
    string Standing,
    bool SuppressesDisconnection,
    string Currency,
    decimal ArrearsBalance,
    decimal DownPayment,
    int InstalmentCount,
    int IntervalDays,
    decimal ScheduledAmount,
    decimal PaidAmount,
    decimal OutstandingAmount,
    DateOnly ArrangedOn,
    DateOnly? ActivatedOn,
    DateOnly? ClosedOn,
    decimal LimitMaximumBalance,
    int LimitMaximumInstalments,
    bool RequiresApproval,
    Guid? ApprovalRequestId,
    string? Notes,
    string ActorId,
    string? ActorName,
    DateTimeOffset RecordedAt,
    IReadOnlyList<ArrangementInstalmentResponse> Instalments)
{
    /// <summary>Projects an arrangement for the wire, judged on <paramref name="asOf"/>.</summary>
    public static PaymentArrangementResponse From(PaymentArrangement arrangement, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(arrangement);

        return new PaymentArrangementResponse(
            arrangement.Id,
            arrangement.ArrangementNumber,
            arrangement.ServiceAccountId,
            arrangement.AccountNumber,
            arrangement.CustomerId,
            arrangement.CustomerName,
            arrangement.CustomerClass.ToString(),
            arrangement.Status.ToString(),
            arrangement.StandingOn(asOf).ToString(),
            arrangement.SuppressesDisconnectionOn(asOf),
            arrangement.Currency,
            arrangement.ArrearsBalance,
            arrangement.DownPayment,
            arrangement.InstalmentCount,
            arrangement.IntervalDays,
            arrangement.ScheduledAmount,
            arrangement.PaidAmount,
            arrangement.OutstandingAmount,
            arrangement.ArrangedOn,
            arrangement.ActivatedOn,
            arrangement.ClosedOn,
            arrangement.LimitMaximumBalance,
            arrangement.LimitMaximumInstalments,
            arrangement.RequiresApproval,
            arrangement.ApprovalRequestId,
            arrangement.Notes,
            arrangement.ActorId,
            arrangement.ActorName,
            arrangement.RecordedAt,
            [
                .. arrangement.Instalments
                    .OrderBy(instalment => instalment.Sequence)
                    .Select(ArrangementInstalmentResponse.From),
            ]);
    }
}

/// <summary>One arrangement a review moved, as the API returns it.</summary>
/// <param name="ArrangementNumber">Which arrangement.</param>
/// <param name="ServiceAccountId">The account it is against.</param>
/// <param name="AccountNumber">Its number.</param>
/// <param name="From">Where it stood before.</param>
/// <param name="To">Where it stands now.</param>
public sealed record ArrangementReviewChangeResponse(
    string ArrangementNumber,
    Guid ServiceAccountId,
    string AccountNumber,
    string From,
    string To);

/// <summary>What one review run did, as the API returns it.</summary>
/// <param name="AsOf">The day it judged against.</param>
/// <param name="Reviewed">How many active arrangements it considered.</param>
/// <param name="BrokenCount">How many it broke.</param>
/// <param name="KeptCount">How many it recorded as kept.</param>
/// <param name="Changes">Every arrangement it moved.</param>
public sealed record ArrangementReviewResponse(
    DateOnly AsOf,
    int Reviewed,
    int BrokenCount,
    int KeptCount,
    IReadOnlyList<ArrangementReviewChangeResponse> Changes)
{
    /// <summary>Projects a run for the wire.</summary>
    public static ArrangementReviewResponse From(ArrangementReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ArrangementReviewResponse(
            result.AsOf,
            result.Reviewed,
            result.BrokenCount,
            result.KeptCount,
            [
                .. result.Changes.Select(change => new ArrangementReviewChangeResponse(
                    change.Arrangement.ArrangementNumber,
                    change.Arrangement.ServiceAccountId,
                    change.Arrangement.AccountNumber,
                    change.From.ToString(),
                    change.To.ToString())),
            ]);
    }
}

/// <summary>Rules for proposing an arrangement.</summary>
/// <remarks>
/// Edge validation catches the shape; the ceilings, the arrears and whether the schedule adds up are
/// the service's and the domain's, because all three depend on the register rather than on the body.
/// </remarks>
public sealed class ProposeArrangementRequestValidator : AbstractValidator<ProposeArrangementRequest>
{
    /// <summary>Builds the rules.</summary>
    public ProposeArrangementRequestValidator()
    {
        RuleFor(request => request.ArrearsBalance).GreaterThan(Money.Zero);
        RuleFor(request => request.DownPayment).GreaterThanOrEqualTo(Money.Zero);

        RuleFor(request => request.InstalmentCount)
            .InclusiveBetween(1, PaymentArrangement.MaximumInstalments);

        RuleFor(request => request.IntervalDays)
            .InclusiveBetween(1, ArrangementSchedule.MaximumIntervalDays);

        RuleFor(request => request.Notes!).MaximumLength(PaymentArrangement.NotesLength);
    }
}

/// <summary>
/// The payment arrangement register's HTTP surface (WP-2.20).
/// </summary>
/// <remarks>
/// <para>
/// <b>Hung off the service account, like WP-2.19's delinquency.</b> An arrangement is always about
/// one supply's arrears at one premise — a customer taking electricity and water may be behind on
/// one and current on the other — so the account is the resource rather than a filter. The two
/// exceptions are the published ceilings and the review run, neither of which is about one account:
/// they sit under <c>/api/payment-arrangements</c>, the shape WP-2.19's late-charge run took.
/// </para>
/// <para>
/// <b>Two gates, and the split follows the package's own line.</b> Reading what has been arranged is
/// <see cref="Permissions.Customers.Read"/>: quoting a schedule down the telephone is what a rep
/// does all day. Making one, bringing it into force and reviewing the register are
/// <see cref="Permissions.Customers.Arrange"/> — a new grant, because an arrangement commits the
/// utility to accept a debt in instalments and suppresses disconnection while it stands, and folding
/// that into <see cref="Permissions.Customers.Write"/> would hand it to every clerk who may correct
/// a spelling. The gate is demanded in the service as well as on the routes, for the reason
/// <see cref="Permissions.Customers.Transition"/>'s is: WP-2.21's disconnection process will reach
/// the service in process, without passing a URL.
/// </para>
/// </remarks>
public static class ArrangementEndpoints
{
    /// <summary>Route prefix of the per-account surface.</summary>
    public const string AccountRoutePrefix = "/api/service-accounts/{serviceAccountId:guid}/payment-arrangements";

    /// <summary>Route prefix of the register-wide surface — the published ceilings and the review run.</summary>
    public const string RegisterRoutePrefix = "/api/payment-arrangements";

    /// <summary>Maps the arrangement endpoints.</summary>
    public static IEndpointRouteBuilder MapArrangementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var account = endpoints.MapGroup(AccountRoutePrefix).WithTags("Payment arrangements");
        var register = endpoints.MapGroup(RegisterRoutePrefix).WithTags("Payment arrangements");

        account
            .MapGet("/", async (
                    [FromRoute] Guid serviceAccountId,
                    int? limit,
                    [FromServices] IPaymentArrangementService arrangements,
                    [FromServices] TimeProvider clock,
                    CancellationToken cancellationToken) =>
            {
                var asOf = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

                return Results.Ok((await arrangements.ListForAccountAsync(
                        serviceAccountId,
                        limit ?? PaymentArrangementService.MaxArrangements,
                        cancellationToken))
                    .Select(arrangement => PaymentArrangementResponse.From(arrangement, asOf))
                    .ToList());
            })
            .RequirePermission(Permissions.Customers.Read)
            .WithName("ListAccountPaymentArrangements");

        account
            .MapPost("/", (
                    [FromRoute] Guid serviceAccountId,
                    ProposeArrangementRequest body,
                    [FromServices] IPaymentArrangementService arrangements,
                    [FromServices] TimeProvider clock,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var arrangement = await arrangements.ProposeAsync(
                        serviceAccountId,
                        new ProposeArrangementInput(
                            body.ArrearsBalance,
                            body.DownPayment,
                            body.InstalmentCount,
                            body.FirstInstalmentDue,
                            body.IntervalDays,
                            body.ArrangedOn,
                            body.Notes),
                        cancellationToken);

                    return Results.Created(
                        $"/api/service-accounts/{serviceAccountId}/payment-arrangements/{arrangement.Id}",
                        PaymentArrangementResponse.From(
                            arrangement,
                            DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)));
                }))
            .RequirePermission(Permissions.Customers.Arrange)
            .WithValidation<ProposeArrangementRequest>()
            .WithName("ProposePaymentArrangement");

        // A sub-resource rather than a PATCH of the status, the shape every non-CRUD action in
        // GridCore takes (CONVENTIONS.md): bringing an arrangement into force is an act with rules
        // behind it — an approval may have to have been granted first — not a field being set.
        account
            .MapPost("/{arrangementId:guid}/activation", (
                    [FromRoute] Guid arrangementId,
                    [FromServices] IPaymentArrangementService arrangements,
                    [FromServices] TimeProvider clock,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(PaymentArrangementResponse.From(
                        await arrangements.ActivateAsync(arrangementId, cancellationToken),
                        DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)))))
            .RequirePermission(Permissions.Customers.Arrange)
            .WithName("ActivatePaymentArrangement");

        register
            .MapGet("/limits", async (
                    [FromServices] IPaymentArrangementService arrangements,
                    CancellationToken cancellationToken) =>
                Results.Ok((await arrangements.LimitsAsync(cancellationToken))
                    .Select(ArrangementLimitResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("ListArrangementLimits");

        // The review run, shaped like WP-2.19's late-charge run: a POST because it writes, with the
        // day it judges against in the body so a run missed on Friday can be re-done for Friday.
        register
            .MapPost("/reviews", (
                    ReviewArrangementsRequest? body,
                    [FromServices] IPaymentArrangementService arrangements,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(ArrangementReviewResponse.From(await arrangements.ReviewAsync(
                        new ReviewArrangementsInput(body?.AsOf, body?.ServiceAccountId),
                        cancellationToken)))))
            .RequirePermission(Permissions.Customers.Arrange)
            .WithName("RunPaymentArrangementReview");

        return endpoints;
    }
}
