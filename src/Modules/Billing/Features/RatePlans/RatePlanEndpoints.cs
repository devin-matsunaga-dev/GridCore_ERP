using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Billing.Features.RatePlans;

/// <summary>Body of a request to put a service account on a tariff.</summary>
/// <param name="RatePlanCode">The tariff code it should bill on, e.g. <c>COM-STD</c>.</param>
public sealed record AssignRatePlanRequest(string RatePlanCode);

/// <summary>One consumption block of a tariff, as the API returns it.</summary>
/// <param name="Sequence">Position in the plan, from 1.</param>
/// <param name="UpToUnits">Cumulative consumption this block covers up to; absent on the last one.</param>
/// <param name="RatePerUnit">Price of one unit inside it.</param>
public sealed record RatePlanTierResponse(int Sequence, decimal? UpToUnits, decimal RatePerUnit)
{
    /// <summary>Projects a tier for the wire.</summary>
    public static RatePlanTierResponse From(RatePlanTier tier)
    {
        ArgumentNullException.ThrowIfNull(tier);

        return new RatePlanTierResponse(tier.Sequence, tier.UpToUnits, tier.RatePerUnit);
    }
}

/// <summary>A published tariff version, as the API returns it.</summary>
/// <param name="Id">Identifier of this version.</param>
/// <param name="Code">The code a person quotes. Shared by every version of the tariff.</param>
/// <param name="Name">What it is called on a bill.</param>
/// <param name="ServiceType">What it charges for.</param>
/// <param name="Currency">ISO 4217 code the charges are expressed in.</param>
/// <param name="UnitOfMeasure">What consumption is measured in.</param>
/// <param name="MonthlyServiceCharge">The fixed charge levied every period.</param>
/// <param name="EffectiveFrom">The first day this version applies.</param>
/// <param name="IsDefault">Whether an account with no tariff of its own bills on it.</param>
/// <param name="Tiers">Its consumption blocks, in order.</param>
public sealed record RatePlanResponse(
    Guid Id,
    string Code,
    string Name,
    string ServiceType,
    string Currency,
    string UnitOfMeasure,
    decimal MonthlyServiceCharge,
    DateOnly EffectiveFrom,
    bool IsDefault,
    IReadOnlyList<RatePlanTierResponse> Tiers)
{
    /// <summary>Projects a tariff version for the wire.</summary>
    public static RatePlanResponse From(RatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new RatePlanResponse(
            plan.Id,
            plan.Code,
            plan.Name,
            plan.ServiceType.ToString(),
            plan.Currency,
            plan.UnitOfMeasure,
            plan.MonthlyServiceCharge,
            plan.EffectiveFrom,
            plan.IsDefault,
            [.. plan.Tiers.OrderBy(tier => tier.Sequence).Select(RatePlanTierResponse.From)]);
    }
}

/// <summary>What an account is billed on, as the API returns it.</summary>
/// <param name="ServiceAccountId">The account asked about.</param>
/// <param name="RatePlanCode">The tariff code it bills on.</param>
/// <param name="IsDefault">Whether that is the fallback rather than a tariff somebody chose.</param>
/// <param name="AssignedAt">When it was chosen, if it was.</param>
/// <param name="ChangedAt">When it was last changed, if ever.</param>
/// <param name="InForce">The version that would price a bill dated today.</param>
public sealed record AccountTariffResponse(
    Guid ServiceAccountId,
    string RatePlanCode,
    bool IsDefault,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? ChangedAt,
    RatePlanResponse? InForce)
{
    /// <summary>Projects an account's tariff for the wire.</summary>
    public static AccountTariffResponse From(AccountTariff tariff, RatePlan? inForce)
    {
        ArgumentNullException.ThrowIfNull(tariff);

        return new AccountTariffResponse(
            tariff.ServiceAccountId,
            tariff.RatePlanCode,
            tariff.IsDefault,
            tariff.AssignedAt,
            tariff.ChangedAt,
            inForce is null ? null : RatePlanResponse.From(inForce));
    }
}

/// <summary>The tariff catalogue's HTTP surface.</summary>
public static class RatePlanEndpoints
{
    /// <summary>Route prefix of the published tariffs.</summary>
    public const string RoutePrefix = "/api/rate-plans";

    /// <summary>Route prefix of the account-to-tariff assignments.</summary>
    public const string AssignmentRoutePrefix = "/api/account-rate-plans";

    /// <summary>Maps the tariff endpoints.</summary>
    public static IEndpointRouteBuilder MapRatePlanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var plans = endpoints.MapGroup(RoutePrefix).WithTags("Billing");

        // Every version of every tariff, oldest first — a tariff's versions read as a history. There
        // is no POST, PUT or DELETE here and there should never be one: tariffs are reference data,
        // so publishing or repricing one is a migration (invariant 7), not a screen.
        plans
            .MapGet("/", async (
                    string? code,
                    [FromServices] IRatePlanService tariffs,
                    CancellationToken cancellationToken) =>
                Results.Ok((await tariffs.ListAsync(code, cancellationToken))
                    .Select(RatePlanResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Billing.Read)
            .WithName("ListRatePlans");

        // The version that would price a bill for a given day — the effective-dating rule, asked
        // directly. `?on=` defaults to today, which is what a screen showing "current rates" wants.
        plans
            .MapGet("/{code}", (
                    [FromRoute] string code,
                    DateOnly? on,
                    [FromServices] IRatePlanService tariffs,
                    [FromServices] TimeProvider clock,
                    CancellationToken cancellationToken) =>
                BillingProblems.RunAsync(async () =>
                    Results.Ok(RatePlanResponse.From(await tariffs.InForceAsync(
                        code,
                        on ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),
                        cancellationToken)))))
            .RequirePermission(Permissions.Billing.Read)
            .WithName("GetRatePlanInForce");

        var assignments = endpoints.MapGroup(AssignmentRoutePrefix).WithTags("Billing");

        // Always answers: an account nobody has assigned bills on the default tariff, and saying so
        // is more useful than a 404 a screen would have to interpret.
        assignments
            .MapGet("/{serviceAccountId:guid}", async (
                    [FromRoute] Guid serviceAccountId,
                    [FromServices] IRatePlanService tariffs,
                    [FromServices] TimeProvider clock,
                    CancellationToken cancellationToken) =>
            {
                var tariff = await tariffs.ForAccountAsync(serviceAccountId, cancellationToken);
                var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                var versions = await tariffs.ListAsync(tariff.RatePlanCode, cancellationToken);

                return Results.Ok(AccountTariffResponse.From(tariff, RatePlanSelector.InForceOn(versions, today)));
            })
            .RequirePermission(Permissions.Billing.Read)
            .WithName("GetAccountRatePlan");

        // A PUT rather than a POST sub-resource: an account is on exactly one tariff, so this sets a
        // value rather than recording an act, and repeating it is idempotent. Gated on
        // billing.generate — see the endpoint tests for why it is not read.
        assignments
            .MapPut("/{serviceAccountId:guid}", (
                    [FromRoute] Guid serviceAccountId,
                    AssignRatePlanRequest body,
                    [FromServices] IRatePlanService tariffs,
                    [FromServices] TimeProvider clock,
                    CancellationToken cancellationToken) =>
                BillingProblems.RunAsync(async () =>
                {
                    var tariff = await tariffs.AssignAsync(serviceAccountId, body.RatePlanCode, cancellationToken);
                    var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                    var versions = await tariffs.ListAsync(tariff.RatePlanCode, cancellationToken);

                    return Results.Ok(AccountTariffResponse.From(tariff, RatePlanSelector.InForceOn(versions, today)));
                }))
            .RequirePermission(Permissions.Billing.Generate)
            .WithValidation<AssignRatePlanRequest>()
            .WithName("AssignAccountRatePlan");

        return endpoints;
    }
}
