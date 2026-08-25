using GridCore.Platform.Monetary;

namespace GridCore.Platform.UnitTests.Monetary;

/// <summary>
/// The one place GridCore rounds money (CONVENTIONS.md: "money <see langword="decimal"/>; centralize
/// rounding in one helper"). Four work packages deferred this helper to WP-2.3 and refused
/// over-precise values in the meantime, so the rule it encodes has to be exactly the one they were
/// waiting for.
/// </summary>
public class MoneyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1.004, 1.00)]
    [InlineData(1.005, 1.01)]
    [InlineData(1.006, 1.01)]
    [InlineData(12.344999, 12.34)]
    [InlineData(-1.005, -1.01)]
    [InlineData(-1.004, -1.00)]
    public void Money_rounds_to_the_cent(double amount, double expected) =>
        Assert.Equal((decimal)expected, Money.Round((decimal)amount));

    [Theory]

    // Halves either side of an even and an odd cent, so the rule is pinned in both directions.
    [InlineData(0.125, 0.13, true)]
    [InlineData(0.135, 0.14, false)]
    [InlineData(2.345, 2.35, true)]
    [InlineData(2.355, 2.36, false)]
    public void Halves_go_away_from_zero_not_to_even(double amount, double expected, bool differsFromBankers)
    {
        // decimal.Round defaults to ToEven, which would answer 0.12 for 0.125. That is right for a
        // long series of measurements and wrong for a document a customer checks by hand: they round
        // halves up, and a utility that answered otherwise would be explaining itself for the rest
        // of the call.
        Assert.Equal((decimal)expected, Money.Round((decimal)amount));

        // The two rules only disagree when the cent below the half is EVEN — 0.125 goes to 0.12
        // under banker's rounding and 0.135 goes to 0.14 under both. Asserting the disagreement on
        // every case would be asserting something untrue half the time, which is how a test that
        // "proves" the rounding mode ends up proving nothing.
        Assert.Equal(differsFromBankers, decimal.Round((decimal)amount, 2) != Money.Round((decimal)amount));
    }

    [Fact]
    public void Rounding_is_idempotent() =>
        Assert.All(
            new[] { 0m, 1.005m, 12.344999m, -8.675m, 1_234_567.891m },
            amount => Assert.Equal(Money.Round(amount), Money.Round(Money.Round(amount))));

    [Theory]
    [InlineData(1.00, true)]
    [InlineData(1.10, true)]
    [InlineData(1.11, true)]
    [InlineData(1.111, false)]
    [InlineData(0.001, false)]
    public void IsRounded_says_whether_a_value_is_already_exact_to_the_cent(double amount, bool expected) =>
        // What a guard asks before REFUSING a value somebody typed. Rounding is for figures GridCore
        // computes; refusal is for figures that arrived from outside, which is the call WP-1.1 made
        // for a deposit, WP-1.3 for a coordinate, WP-1.4 for a quantity and WP-2.1 for an
        // installation reading.
        Assert.Equal(expected, Money.IsRounded((decimal)amount));

    [Fact]
    public void A_trailing_zero_does_not_make_a_value_over_precise() =>
        // 1.10m and 1.100m are equal but carry different scales in decimal. A guard that compared
        // scales rather than values would refuse a perfectly ordinary amount.
        Assert.True(Money.IsRounded(1.100m));

    [Fact]
    public void Totalling_rounded_amounts_is_exact()
    {
        // The reason a bill's total is the sum of its printed lines rather than a separately
        // rounded figure: adding cents in decimal loses nothing, so the document adds up.
        decimal[] lines = [12.50m, 57.25m, 69.25m, 81.00m];

        Assert.Equal(220.00m, Money.Total(lines));
        Assert.True(Money.IsRounded(Money.Total(lines)));
    }

    [Fact]
    public void Totalling_nothing_is_nothing() =>
        Assert.Equal(Money.Zero, Money.Total([]));

    [Fact]
    public void A_float_would_have_lost_this_and_decimal_does_not()
    {
        // Invariant 4, demonstrated rather than asserted: 0.1 + 0.2 is not 0.3 in binary floating
        // point, and a utility whose ledger drifted by a cent per bill would find out at the trial
        // balance.
        Assert.Equal(0.30m, Money.Round(0.1m + 0.2m));
        Assert.NotEqual(0.3, 0.1 + 0.2);
    }
}
