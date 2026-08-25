using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Rating;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Billing.UnitTests.Rating;

/// <summary>
/// The tiered arithmetic at the centre of the revenue cycle, tested exhaustively and with no
/// database — which is the whole reason <see cref="RateEngine"/> is pure (CONVENTIONS.md rule C).
/// Every boundary of both shipped tariffs is pinned on both sides.
/// </summary>
public class RateEngineTests
{
    private static RatePlan Residential => DefaultRatePlans.VersionsOf(DefaultRatePlans.ResidentialStandard)[0];

    private static RatePlan Commercial => DefaultRatePlans.Require(DefaultRatePlans.CommercialStandard);

    private static RateCalculation Charge(RatePlan plan, decimal consumption) =>
        RateEngine.Calculate(plan, DefaultRatePlans.TiersOf(plan), consumption);

    [Theory]

    // Inclining block: 500 @ 0.1145, then 500 @ 0.1385, then the rest @ 0.1620, plus 12.50 standing.
    [InlineData(0, 12.50)]
    [InlineData(1, 12.61)]         // 0.1145 → 0.11
    [InlineData(100, 23.95)]       // 11.45
    [InlineData(499, 69.64)]       // 57.14
    [InlineData(500, 69.75)]       // 57.25 — the last unit of tier 1
    [InlineData(501, 69.89)]       // 57.25 + 0.14 — the first unit of tier 2
    [InlineData(750, 104.38)]      // 57.25 + 34.63
    [InlineData(999, 138.86)]      // 57.25 + 69.11
    [InlineData(1000, 139.00)]     // 57.25 + 69.25 — the last unit of tier 2
    [InlineData(1001, 139.16)]     // 57.25 + 69.25 + 0.16 — the first unit of tier 3
    [InlineData(1500, 220.00)]     // 57.25 + 69.25 + 81.00
    [InlineData(10000, 1597.00)]   // 57.25 + 69.25 + 1458.00
    public void The_residential_tariff_charges_each_block_at_its_own_rate(double consumption, double expected) =>
        // Blocks are CUMULATIVE, not per-tier allowances: 600 units is 500 in tier 1 and 100 in
        // tier 2, never 600 in each. That is the classic tiered-rate bug and this table is what
        // stops it coming back.
        Assert.Equal((decimal)expected, Charge(Residential, (decimal)consumption).Total);

    [Theory]

    // Declining block: 2 000 @ 0.1290, then the rest @ 0.1105, plus 45.00 standing.
    [InlineData(0, 45.00)]
    [InlineData(1999, 302.87)]     // 257.87
    [InlineData(2000, 303.00)]     // 258.00 — the last unit of tier 1
    [InlineData(2001, 303.11)]     // 258.00 + 0.11 — the first unit of tier 2
    [InlineData(5000, 634.50)]     // 258.00 + 331.50
    public void The_commercial_tariff_charges_volume_more_cheaply(double consumption, double expected) =>
        Assert.Equal((decimal)expected, Charge(Commercial, (decimal)consumption).Total);

    [Fact]
    public void A_bill_always_equals_the_sum_of_its_own_printed_lines()
    {
        // THE MONEY GUARD, at the level the engine owes it. Rounding once at the end would produce
        // a document whose lines do not add up to its total — the first thing a customer checks.
        foreach (var consumption in new[] { 0m, 1m, 333.333m, 500m, 501m, 1_000m, 1_437.219m, 9_999.999m })
        {
            var calculation = Charge(Residential, consumption);

            Assert.Equal(calculation.Total, Money.Total(calculation.Charges.Select(charge => charge.Amount)));
            Assert.Equal(calculation.Total, calculation.ServiceCharge + calculation.ConsumptionTotal);
        }
    }

    [Fact]
    public void Every_amount_is_exact_to_the_cent()
    {
        var calculation = Charge(Residential, 1_437.219m);

        Assert.All(calculation.Charges, charge => Assert.True(Money.IsRounded(charge.Amount)));
        Assert.True(Money.IsRounded(calculation.Total));
    }

    [Fact]
    public void No_consumption_still_bills_the_standing_charge_and_nothing_else()
    {
        // The difference between an empty house and one that is not connected. A tier that covers
        // no units produces no line at all — "0 kWh @ 0.1620 = 0.00" is noise on a document meant
        // to be read.
        var calculation = Charge(Residential, 0m);

        var line = Assert.Single(calculation.Charges);

        Assert.Equal(ChargeKind.ServiceCharge, line.Kind);
        Assert.Equal(12.50m, calculation.Total);
        Assert.Equal(Money.Zero, calculation.ConsumptionTotal);
    }

    [Fact]
    public void Consumption_inside_one_block_produces_one_consumption_line()
    {
        var calculation = Charge(Residential, 250m);

        Assert.Equal(2, calculation.Charges.Count);
        Assert.Equal([ChargeKind.ServiceCharge, ChargeKind.Consumption], calculation.Charges.Select(charge => charge.Kind));
        Assert.Equal(1, calculation.Charges[1].TierSequence);
        Assert.Equal(250m, calculation.Charges[1].Units);
    }

    [Fact]
    public void Consumption_spanning_every_block_produces_a_line_for_each()
    {
        var calculation = Charge(Residential, 1_500m);

        Assert.Equal(4, calculation.Charges.Count);
        Assert.Equal([1, 2, 3], calculation.Charges.Skip(1).Select(charge => charge.TierSequence));

        // The units are the BLOCK's share, and they add up to what was consumed.
        Assert.Equal([500m, 500m, 500m], calculation.Charges.Skip(1).Select(charge => charge.Units));
        Assert.Equal(1_500m, calculation.Charges.Skip(1).Sum(charge => charge.Units));
    }

    [Fact]
    public void Lines_are_numbered_in_the_order_they_are_printed()
    {
        var calculation = Charge(Residential, 1_500m);

        Assert.Equal([1, 2, 3, 4], calculation.Charges.Select(charge => charge.Sequence));
    }

    [Fact]
    public void Each_line_carries_the_rate_that_produced_it()
    {
        // Stamped, not looked up. The tariff will be repriced, and a bill that re-derived its own
        // arithmetic from today's rates would silently change what a customer was charged.
        var calculation = Charge(Residential, 1_500m);

        Assert.Equal([0.1145m, 0.1385m, 0.1620m], calculation.Charges.Skip(1).Select(charge => charge.RatePerUnit));
        Assert.Null(calculation.Charges[0].RatePerUnit);
        Assert.Null(calculation.Charges[0].Units);
    }

    [Fact]
    public void The_calculation_names_the_tariff_version_it_used()
    {
        // What makes a bill reproducible: the code says which tariff, the effective date says which
        // version of it.
        var calculation = Charge(Residential, 100m);

        Assert.Equal(Residential.Id, calculation.RatePlanId);
        Assert.Equal(DefaultRatePlans.ResidentialStandard, calculation.RatePlanCode);
        Assert.Equal(DefaultRatePlans.OriginalEffectiveFrom, calculation.EffectiveFrom);
        Assert.Equal("USD", calculation.Currency);
        Assert.Equal("kWh", calculation.UnitOfMeasure);
    }

    [Fact]
    public void The_repriced_residential_tariff_charges_more_for_the_same_units()
    {
        // Effective dating with money attached: the same consumption on the two published versions
        // must not come out the same, or every test that picks a version proves nothing.
        var versions = DefaultRatePlans.VersionsOf(DefaultRatePlans.ResidentialStandard);

        Assert.True(Charge(versions[1], 750m).Total > Charge(versions[0], 750m).Total);
    }

    [Fact]
    public void Consumption_to_three_places_is_billed_to_three_places()
    {
        // The reading register stores consumption at numeric(18,3), so the engine has to charge it
        // at that precision rather than rounding the units before pricing them.
        var calculation = Charge(Residential, 123.456m);

        Assert.Equal(123.456m, calculation.Charges[1].Units);
        Assert.Equal(123.456m, calculation.Consumption);

        // 123.456 * 0.1145 = 14.135712 → 14.14 (half away from zero), plus 12.50.
        Assert.Equal(26.64m, calculation.Total);
    }

    [Fact]
    public void Halves_round_away_from_zero_not_to_even()
    {
        // The demonstrable rule beats the statistically neutral one wherever a person has to agree
        // with the answer. 0.125 is 0.13 on a bill, whatever decimal.Round would say by default.
        var tiers = new[] { RatePlanTier.Reference("HALF@2026-01-01", Residential.Id, 1, null, 0.025m) };

        var plan = RatePlan.Reference(
            "HALF", "Halves", ServiceType.Electricity, "USD", "kWh", 0m, new DateOnly(2026, 1, 1), isDefault: false);

        // 5 * 0.025 = 0.125, which banker's rounding would make 0.12.
        Assert.Equal(0.13m, RateEngine.Calculate(plan, tiers, 5m).Total);
    }

    [Fact]
    public void Negative_consumption_is_refused()
    {
        // Failure path. Unreachable from a reading — rollover is handled where consumption is
        // measured, so a wrapped register reports units used — which is exactly why a negative
        // arriving here means something upstream broke and must be loud.
        var exception = Assert.Throws<BillingValidationException>(() => Charge(Residential, -1m));

        Assert.Contains("cannot be negative", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Consumption_finer_than_the_reading_register_is_refused()
    {
        // Refused, not rounded: this is a figure that arrived from outside, and rounding it would
        // bill units no meter ever recorded. The same call WP-1.1 made for a deposit finer than a
        // cent, now that the rounding helper exists and could have been used instead.
        var exception = Assert.Throws<BillingValidationException>(() => Charge(Residential, 100.0001m));

        Assert.Contains("finer than that", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tariff_whose_tiers_do_not_form_a_usable_set_is_refused()
    {
        // Failure path: a malformed tariff is discovered here rather than on a customer's bill.
        // The shipped plans are validated at type-initialisation, so this can only be reached by a
        // plan built in code — a later admin screen, or a test like this one.
        var plan = RatePlan.Reference(
            "BOUNDED", "Ends too soon", ServiceType.Electricity, "USD", "kWh", 1m, new DateOnly(2026, 1, 1), isDefault: false);

        // The last tier is bounded, so consumption above 100 would be billed at no rate at all.
        var tiers = new[] { RatePlanTier.Reference("BOUNDED@2026-01-01", plan.Id, 1, 100m, 0.5m) };

        Assert.Throws<BillingValidationException>(() => RateEngine.Calculate(plan, tiers, 50m));
    }

    [Fact]
    public void A_tariff_with_no_tiers_is_refused() =>
        Assert.Throws<BillingValidationException>(() => RateEngine.Calculate(Residential, [], 100m));

    [Fact]
    public void Tiers_out_of_order_are_charged_in_sequence_order()
    {
        // The engine sorts rather than trusting the order it was handed: a tariff loaded through an
        // Include comes back in whatever order the database chose, and applying the blocks in the
        // wrong order would charge the wrong rate for every unit.
        var shuffled = DefaultRatePlans.TiersOf(Residential).Reverse().ToList();

        Assert.Equal(
            Charge(Residential, 1_500m).Total,
            RateEngine.Calculate(Residential, shuffled, 1_500m).Total);
    }
}
