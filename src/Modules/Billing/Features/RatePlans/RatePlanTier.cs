using GridCore.Platform.Data;

namespace GridCore.Modules.Billing.Features.RatePlans;

/// <summary>
/// One consumption block of a tariff: everything up to <see cref="UpToUnits"/> is charged at
/// <see cref="RatePerUnit"/>.
/// </summary>
public sealed class RatePlanTier
{
    /// <summary>Identifier of this tier.</summary>
    public Guid Id { get; private init; }

    /// <summary>The plan this tier belongs to.</summary>
    public Guid RatePlanId { get; private init; }

    /// <summary>Position in the plan, from 1. Tiers are applied in this order.</summary>
    public int Sequence { get; private init; }

    /// <summary>
    /// The cumulative consumption this tier covers up to, or <see langword="null"/> for the final,
    /// unbounded tier. Every plan ends in one, so consumption can never exceed its own tariff.
    /// </summary>
    public decimal? UpToUnits { get; private init; }

    /// <summary>Price of one unit inside this tier. Money is <see langword="decimal"/>, never a float.</summary>
    public decimal RatePerUnit { get; private init; }

    /// <summary>
    /// Builds a reference tier. The id is derived from the plan code and the sequence, so the
    /// migration seeds the same rows every time it is generated.
    /// </summary>
    /// <exception cref="ArgumentException">The sequence, bound or rate is out of range.</exception>
    public static RatePlanTier Reference(string planCode, Guid ratePlanId, int sequence, decimal? upToUnits, decimal ratePerUnit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planCode);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);

        if (upToUnits is <= 0m)
        {
            throw new ArgumentException(
                $"Tier {sequence} of rate plan '{planCode}' ends at {upToUnits}, which covers no consumption.",
                nameof(upToUnits));
        }

        // A negative rate would pay customers to consume, which is not a tariff anyone publishes by
        // accident — so it is refused where the plan is built rather than discovered on a bill.
        if (ratePerUnit < 0m)
        {
            throw new ArgumentException(
                $"Tier {sequence} of rate plan '{planCode}' has a negative rate ({ratePerUnit}).",
                nameof(ratePerUnit));
        }

        return new RatePlanTier
        {
            Id = ReferenceId.For(DefaultRatePlans.AuthoredAt, $"{planCode}#{sequence}"),
            RatePlanId = ratePlanId,
            Sequence = sequence,
            UpToUnits = upToUnits,
            RatePerUnit = ratePerUnit,
        };
    }
}
