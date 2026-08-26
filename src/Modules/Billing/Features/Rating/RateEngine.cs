using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Billing.Features.Rating;

/// <summary>What kind of charge a bill line is.</summary>
/// <remarks>
/// Stored by name on every line, so a bill read back years from now does not depend on today's enum
/// ordering — the rule every stored enum in GridCore follows.
/// </remarks>
public enum ChargeKind
{
    /// <summary>The fixed charge levied every period regardless of consumption.</summary>
    ServiceCharge = 1,

    /// <summary>Units consumed inside one tier of the tariff, at that tier's rate.</summary>
    Consumption = 2,

    /// <summary>
    /// A fee from the published schedule, raised against the account and landed on this bill
    /// (WP-2.16) — a reconnection, a returned payment, a meter test.
    /// </summary>
    /// <remarks>
    /// <b>Not per unit, and that is what tells it from a consumption line.</b> A fee carries no
    /// tier, no units and no rate: it is a published figure, priced by the schedule row in force on
    /// the day it was raised rather than by anything the meter did. The rate engine never produces
    /// one — <c>RateEngine.Calculate</c> knows about a tariff and nothing else — so a fee line
    /// reaches a bill only through a charge somebody raised.
    /// </remarks>
    Fee = 3,
}

/// <summary>
/// One line of a bill as the rate engine computed it: what it is for, how many units at what rate,
/// and what that comes to.
/// </summary>
/// <param name="Sequence">Position on the bill, from 1.</param>
/// <param name="Kind">Whether this is the standing charge or a consumption block.</param>
/// <param name="Description">What the line says on the bill.</param>
/// <param name="TierSequence">Which tier of the tariff produced it, for a consumption line.</param>
/// <param name="Units">Units charged. Absent on the service charge, which is not per unit.</param>
/// <param name="RatePerUnit">Price of one unit inside the tier. Absent on the service charge.</param>
/// <param name="Amount">What the line comes to, already rounded to the cent.</param>
public sealed record RateCharge(
    int Sequence,
    ChargeKind Kind,
    string Description,
    int? TierSequence,
    decimal? Units,
    decimal? RatePerUnit,
    decimal Amount);

/// <summary>What a tariff makes of a period's consumption.</summary>
/// <param name="RatePlanId">The plan version the charges were computed from.</param>
/// <param name="RatePlanCode">Its code, as printed on the bill.</param>
/// <param name="RatePlanName">Its name, as printed on the bill.</param>
/// <param name="EffectiveFrom">The day that version took effect — why these rates and not others.</param>
/// <param name="Currency">ISO 4217 code the amounts are expressed in.</param>
/// <param name="UnitOfMeasure">What the units are measured in.</param>
/// <param name="Consumption">Units billed.</param>
/// <param name="Charges">The lines, in order.</param>
/// <param name="Total">
/// What the bill comes to — the sum of the lines <i>as printed</i>, never a separately rounded
/// figure. See <see cref="Money"/> for why the two must not be computed independently.
/// </param>
public sealed record RateCalculation(
    Guid RatePlanId,
    string RatePlanCode,
    string RatePlanName,
    DateOnly EffectiveFrom,
    string Currency,
    string UnitOfMeasure,
    decimal Consumption,
    IReadOnlyList<RateCharge> Charges,
    decimal Total)
{
    /// <summary>What the consumption alone came to, without the standing charge.</summary>
    public decimal ConsumptionTotal =>
        Money.Total(Charges.Where(charge => charge.Kind is ChargeKind.Consumption).Select(charge => charge.Amount));

    /// <summary>The standing charge levied.</summary>
    public decimal ServiceCharge =>
        Money.Total(Charges.Where(charge => charge.Kind is ChargeKind.ServiceCharge).Select(charge => charge.Amount));
}

/// <summary>
/// Turns a period's consumption into the charges a tariff makes of it. The arithmetic at the centre
/// of the revenue cycle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure and static.</b> No database, no clock, no services — a plan, its tiers, a number of
/// units, and an answer. That is deliberate and is what CONVENTIONS.md's ⚡ rules ask for: the
/// arithmetic a customer will dispute is the part that must be tested exhaustively, and here it can
/// be, in milliseconds, across every tier boundary.
/// </para>
/// <para>
/// <b>Blocks are cumulative, not per-tier allowances.</b> <see cref="RatePlanTier.UpToUnits"/> is
/// where the tier <i>stops counting from zero</i>, so 600 kWh on the residential tariff is 500 units
/// in tier 1 and 100 in tier 2 — not 600 in each. Getting that backwards is the classic tiered-rate
/// bug and there is a test on each side of every shipped boundary.
/// </para>
/// <para>
/// <b>Each line is rounded, and the total is their sum.</b> Rounding once at the end would produce
/// a bill whose printed lines do not add up to its own total, which is the first thing a customer
/// checks and the last thing a utility wants to explain. See <see cref="Money"/>.
/// </para>
/// <para>
/// <b>A tier that covers no units produces no line.</b> A bill reading "0 kWh @ 0.1620 = 0.00" for
/// two blocks nobody reached is noise on a document meant to be read. The standing charge is always
/// there, so a period of no consumption still bills — which is correct, and is the difference
/// between an empty house and one that is not connected.
/// </para>
/// </remarks>
public static class RateEngine
{
    /// <summary>Decimal places consumption carries, matching the reading register it comes from.</summary>
    public const int ConsumptionDecimalPlaces = 3;

    /// <summary>What the standing charge line says on a bill.</summary>
    public const string ServiceChargeDescription = "Monthly service charge";

    /// <summary>
    /// Charges <paramref name="consumption"/> against <paramref name="plan"/>.
    /// </summary>
    /// <param name="plan">The tariff version in force for the period — see <see cref="RatePlanSelector"/>.</param>
    /// <param name="tiers">Its tiers. Validated as a set before anything is charged.</param>
    /// <param name="consumption">Units used over the period.</param>
    /// <exception cref="BillingValidationException">
    /// The consumption is negative or finer than the reading register stores, or the tariff's tiers
    /// do not form a usable set.
    /// </exception>
    public static RateCalculation Calculate(RatePlan plan, IReadOnlyList<RatePlanTier> tiers, decimal consumption)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(tiers);

        // Every guard before the first arithmetic — WP-1.4's ordering rule. Nothing here mutates,
        // but a half-computed bill handed back to a caller is the same lie in a different shape.
        if (consumption < 0m)
        {
            // Unreachable from a reading (rollover is handled where consumption is measured, so a
            // wrapped register reports units used rather than a negative), which is exactly why it
            // is worth refusing loudly: a negative arriving here means something upstream broke.
            throw new BillingValidationException(
                $"Consumption cannot be negative; rate plan '{plan.Code}' was asked to charge {consumption}.");
        }

        if (decimal.Round(consumption, ConsumptionDecimalPlaces) != consumption)
        {
            throw new BillingValidationException(
                $"Consumption is measured to {ConsumptionDecimalPlaces} decimal places; '{consumption}' is finer than that.");
        }

        try
        {
            RatePlanTiers.Validate(plan.VersionKey, tiers);
        }
        catch (ArgumentException exception)
        {
            // Translated rather than propagated: a malformed tariff is this module's failure to
            // answer, and every caller here already handles BillingRegistryException.
            throw new BillingValidationException(exception.Message);
        }

        var charges = new List<RateCharge>(tiers.Count + 1)
        {
            new(
                Sequence: 1,
                ChargeKind.ServiceCharge,
                ServiceChargeDescription,
                TierSequence: null,
                Units: null,
                RatePerUnit: null,
                Money.Round(plan.MonthlyServiceCharge)),
        };

        // Where the previous block stopped. Blocks are cumulative from zero, so this is what turns
        // "up to 1 000" into "the 500 units between 500 and 1 000".
        var chargedTo = 0m;

        foreach (var tier in tiers.OrderBy(tier => tier.Sequence))
        {
            // The last tier is unbounded, so it takes whatever is left — which is what makes it
            // impossible for consumption to fall off the end of a tariff (RatePlanTiers.Validate).
            var tierCeiling = tier.UpToUnits ?? consumption;
            var units = Math.Min(consumption, tierCeiling) - chargedTo;

            if (units <= 0m)
            {
                // Consumption did not reach this block. Nothing to charge and nothing to print —
                // and no `break`, because a tier is skipped on its own merits rather than on an
                // assumption about the ones after it.
                continue;
            }

            charges.Add(new RateCharge(
                charges.Count + 1,
                ChargeKind.Consumption,
                Describe(tier, chargedTo, plan.UnitOfMeasure),
                tier.Sequence,
                units,
                tier.RatePerUnit,
                Money.Round(units * tier.RatePerUnit)));

            chargedTo += units;
        }

        return new RateCalculation(
            plan.Id,
            plan.Code,
            plan.Name,
            plan.EffectiveFrom,
            plan.Currency,
            plan.UnitOfMeasure,
            consumption,
            charges,
            Money.Total(charges.Select(charge => charge.Amount)));
    }

    /// <summary>
    /// What a consumption line says on the bill — the block it covers, in the units the tariff is
    /// measured in, so a customer can check which rate applied to which part of their usage.
    /// </summary>
    private static string Describe(RatePlanTier tier, decimal from, string unitOfMeasure) =>
        tier.UpToUnits is { } upTo
            ? $"Consumption {from:0.###}–{upTo:0.###} {unitOfMeasure}"
            : $"Consumption above {from:0.###} {unitOfMeasure}";
}
