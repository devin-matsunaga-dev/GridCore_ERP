using GridCore.Modules.Billing.Features.RatePlans;

namespace GridCore.Modules.Billing.UnitTests.RatePlans;

/// <summary>
/// The rules that make a tariff billable. Every one of these is a way a bill could come out wrong,
/// caught in milliseconds where the plan is built rather than on a customer's statement.
/// </summary>
public class RatePlanTiersTests
{
    private static readonly Guid PlanId = Guid.CreateVersion7();

    private static RatePlanTier Tier(int sequence, decimal? upTo, decimal rate = 0.10m) =>
        RatePlanTier.Reference("TEST", PlanId, sequence, upTo, rate);

    [Fact]
    public void A_well_formed_tariff_passes()
    {
        RatePlanTiers.Validate("TEST", [Tier(1, 500m), Tier(2, 1_000m), Tier(3, null)]);
    }

    [Fact]
    public void A_single_unbounded_tier_is_a_flat_tariff_and_is_fine()
    {
        RatePlanTiers.Validate("TEST", [Tier(1, null)]);
    }

    [Fact]
    public void Tiers_may_be_given_in_any_order()
    {
        RatePlanTiers.Validate("TEST", [Tier(3, null), Tier(1, 500m), Tier(2, 1_000m)]);
    }

    [Fact]
    public void A_plan_with_no_tiers_is_refused()
    {
        var refused = Assert.Throws<ArgumentException>(() => RatePlanTiers.Validate("TEST", []));

        Assert.Contains("no tiers", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bounded_last_tier_is_refused()
    {
        // The failure that matters most: consumption above the last bound would be billed at no
        // rate at all, so a heavy user's bill would silently understate what they owe.
        var refused = Assert.Throws<ArgumentException>(
            () => RatePlanTiers.Validate("TEST", [Tier(1, 500m), Tier(2, 1_000m)]));

        Assert.Contains("unbounded", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unbounded_tier_that_is_not_last_is_refused()
    {
        var refused = Assert.Throws<ArgumentException>(
            () => RatePlanTiers.Validate("TEST", [Tier(1, null), Tier(2, 1_000m), Tier(3, null)]));

        Assert.Contains("could never be reached", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gap_in_the_tier_numbering_is_refused()
    {
        var refused = Assert.Throws<ArgumentException>(
            () => RatePlanTiers.Validate("TEST", [Tier(1, 500m), Tier(3, null)]));

        Assert.Contains("1..n", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_tiers_with_the_same_number_are_refused()
    {
        var refused = Assert.Throws<ArgumentException>(
            () => RatePlanTiers.Validate("TEST", [Tier(1, 500m), Tier(1, 800m), Tier(2, null)]));

        Assert.Contains("1..n", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tier_that_does_not_end_above_the_previous_one_is_refused()
    {
        // Overlapping blocks mean the same kWh is priced twice, and which price wins depends on the
        // order the engine happens to walk them in.
        var refused = Assert.Throws<ArgumentException>(
            () => RatePlanTiers.Validate("TEST", [Tier(1, 500m), Tier(2, 500m), Tier(3, null)]));

        Assert.Contains("not above", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tier_covering_no_consumption_is_refused_where_it_is_built()
    {
        Assert.Throws<ArgumentException>(() => Tier(1, 0m));
    }

    [Fact]
    public void A_negative_rate_is_refused()
    {
        // A tariff that pays customers to consume is not something anyone publishes on purpose.
        Assert.Throws<ArgumentException>(() => Tier(1, 500m, rate: -0.05m));
    }
}
