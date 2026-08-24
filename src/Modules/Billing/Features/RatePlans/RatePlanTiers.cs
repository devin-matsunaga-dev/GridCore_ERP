namespace GridCore.Modules.Billing.Features.RatePlans;

/// <summary>
/// The rules a plan's tiers must satisfy to be billable at all.
/// </summary>
/// <remarks>
/// Pure and static, so the fast tier proves them without a database (CONVENTIONS.md rule C). They
/// are checked where a plan is built rather than where it is used, because a malformed tariff is
/// discovered on a customer's bill otherwise — the most expensive place to find it. WP-2.3's rate
/// engine consumes tiers that have already passed this.
/// </remarks>
public static class RatePlanTiers
{
    /// <summary>
    /// Checks that <paramref name="tiers"/> form a usable tariff: numbered 1..n without gaps, each
    /// ending above the last, and ending unbounded so no consumption falls off the end.
    /// </summary>
    /// <param name="planCode">The plan being validated, named in any failure.</param>
    /// <param name="tiers">The plan's tiers, in any order.</param>
    /// <exception cref="ArgumentException">The tiers do not form a usable tariff.</exception>
    public static void Validate(string planCode, IReadOnlyList<RatePlanTier> tiers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planCode);
        ArgumentNullException.ThrowIfNull(tiers);

        if (tiers.Count is 0)
        {
            throw new ArgumentException($"Rate plan '{planCode}' has no tiers, so it can bill nothing.", nameof(tiers));
        }

        var ordered = tiers.OrderBy(tier => tier.Sequence).ToList();

        // Where the previous tier stopped. Starting at zero means the first tier is held to the
        // same rule as the rest — it must cover some consumption — with no special case.
        var previousBound = 0m;

        for (var index = 0; index < ordered.Count; index++)
        {
            var tier = ordered[index];

            if (tier.Sequence != index + 1)
            {
                throw new ArgumentException(
                    $"Rate plan '{planCode}' has tiers numbered {string.Join(", ", ordered.Select(t => t.Sequence))}; "
                    + "they must run 1..n without gaps or duplicates.",
                    nameof(tiers));
            }

            var isLast = index == ordered.Count - 1;

            if (isLast)
            {
                if (tier.UpToUnits is not null)
                {
                    throw new ArgumentException(
                        $"The last tier of rate plan '{planCode}' ends at {tier.UpToUnits}; it must be unbounded, "
                        + "or consumption above that would be billed at no rate at all.",
                        nameof(tiers));
                }

                continue;
            }

            if (tier.UpToUnits is not { } bound)
            {
                throw new ArgumentException(
                    $"Tier {tier.Sequence} of rate plan '{planCode}' is unbounded but is not the last tier, "
                    + "so the tiers after it could never be reached.",
                    nameof(tiers));
            }

            if (bound <= previousBound)
            {
                throw new ArgumentException(
                    $"Tier {tier.Sequence} of rate plan '{planCode}' ends at {bound}, which is not above where the "
                    + $"previous tier ended ({previousBound}).",
                    nameof(tiers));
            }

            previousBound = bound;
        }
    }
}
