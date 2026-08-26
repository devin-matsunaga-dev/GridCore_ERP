using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Fees;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Rating;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Billing.UnitTests.Fees;

/// <summary>
/// Fees on a bill, at the aggregate: what a fee line may carry, what a charge bill looks like, and
/// the money guard with fees added to it. Pure — no host, no database (CONVENTIONS.md rule C).
/// </summary>
public class BillFeeLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private static readonly RegistryActor Actor = new("auth0|clerk", "A customer service rep");

    private static ServiceAccountSummary Account() => new(
        Guid.CreateVersion7(),
        "A-000001",
        Guid.CreateVersion7(),
        "Rosa Sablan",
        Guid.CreateVersion7(),
        "Active",
        HoldsPremise: true,
        ServiceStartedAt: Now.AddYears(-1));

    private static RateCharge Fee(string description, decimal amount) =>
        new(Sequence: 0, ChargeKind.Fee, description, TierSequence: null, Units: null, RatePerUnit: null, amount);

    private static RateCalculation Calculation(params RateCharge[] charges) => new(
        Guid.CreateVersion7(),
        DefaultRatePlans.ResidentialStandard,
        "Residential standard",
        DefaultRatePlans.OriginalEffectiveFrom,
        "USD",
        "kWh",
        Consumption: 100m,
        charges,
        Money.Total(charges.Select(charge => charge.Amount)));

    private static RateCalculation ASupplyCalculation() => Calculation(
        new RateCharge(1, ChargeKind.ServiceCharge, RateEngine.ServiceChargeDescription, null, null, null, 12.50m),
        new RateCharge(2, ChargeKind.Consumption, "Consumption 0–100 kWh", 1, 100m, 0.1145m, 11.45m));

    private static Bill Cycle(IReadOnlyList<RateCharge>? fees = null) => Bill.Calculate(
        "BIL-000001",
        Account(),
        new BilledReading(Guid.CreateVersion7(), Guid.CreateVersion7(), "MTR-000001", 100m, 200m),
        ASupplyCalculation(),
        Today.AddDays(-30),
        Today,
        Actor,
        Now,
        "2026-08",
        fees);

    [Fact]
    public void A_cycle_bill_with_no_fees_is_exactly_what_it_was_before()
    {
        var bill = Cycle();

        Assert.Equal(BillKind.Consumption, bill.Kind);
        Assert.Equal(0m, bill.FeeAmount);
        Assert.Equal(23.95m, bill.TotalAmount);
        Assert.Equal(2, bill.Lines.Count);
    }

    [Fact]
    public void A_fee_lands_after_the_tariffs_own_lines_and_is_numbered_in_one_series()
    {
        // Where a fee sits is the document's business: the charge arrives unnumbered and the bill
        // numbers it, so nothing can disagree with the document it landed on.
        var bill = Cycle([Fee("Reconnection fee", 60.00m), Fee("Meter test fee", 75.00m)]);

        Assert.Equal([1, 2, 3, 4], bill.Lines.Select(line => line.Sequence));
        Assert.Equal(
            [ChargeKind.ServiceCharge, ChargeKind.Consumption, ChargeKind.Fee, ChargeKind.Fee],
            bill.Lines.Select(line => line.Kind));
    }

    [Fact]
    public void A_bill_with_fees_totals_the_supply_and_the_fees_and_still_equals_its_own_lines()
    {
        // THE MONEY GUARD, with fees added to it. A bill equals the sum of what is printed on it,
        // and the fee half is recorded separately so Finance can credit the right revenue account.
        var bill = Cycle([Fee("Reconnection fee", 60.00m)]);

        Assert.Equal(83.95m, bill.TotalAmount);
        Assert.Equal(60.00m, bill.FeeAmount);
        Assert.Equal(bill.TotalAmount, Money.Total(bill.Lines.Select(line => line.Amount)));
    }

    [Fact]
    public void A_fee_line_carries_no_tier_no_units_and_no_rate()
    {
        // WHAT DISTINGUISHES A FEE FROM CONSUMPTION. A fee is a published figure, not a quantity at
        // a price, and the three per-unit fields are what say so on the stored line.
        var fee = Cycle([Fee("Reconnection fee", 60.00m)]).Lines.Single(line => line.Kind is ChargeKind.Fee);

        Assert.Null(fee.TierSequence);
        Assert.Null(fee.Units);
        Assert.Null(fee.RatePerUnit);
        Assert.Equal(60.00m, fee.Amount);
    }

    [Theory]
    [InlineData(ChargeKind.ServiceCharge)]
    [InlineData(ChargeKind.Consumption)]
    public void A_line_that_is_not_a_fee_cannot_be_landed_this_way(ChargeKind kind)
    {
        // Only a fee lands on a bill through a charge; consumption comes from the rate engine. A
        // caller that built one wrongly has a defect worth seeing rather than silently absorbing.
        var smuggled = new RateCharge(0, kind, "Smuggled in", null, null, null, 10.00m);

        Assert.Throws<BillingValidationException>(() => Cycle([smuggled]));
    }

    [Fact]
    public void A_fee_that_arrives_with_units_on_it_is_refused()
    {
        var priced = new RateCharge(0, ChargeKind.Fee, "Reconnection fee", TierSequence: 1, Units: 4m, RatePerUnit: 15m, 60.00m);

        var refusal = Assert.Throws<BillingValidationException>(() => Cycle([priced]));

        Assert.Contains("published figure", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-25)]
    public void A_fee_of_nothing_or_less_is_refused(decimal amount) =>
        Assert.Throws<BillingValidationException>(() => Cycle([Fee("Reconnection fee", amount)]));

    [Fact]
    public void A_fee_finer_than_a_cent_is_refused() =>
        Assert.Throws<BillingValidationException>(() => Cycle([Fee("Reconnection fee", 60.005m)]));

    [Fact]
    public void A_charge_bill_has_no_meter_no_tariff_and_no_units()
    {
        var bill = Bill.ForCharges("BIL-000002", Account(), [Fee("Reconnection fee", 60.00m)], "USD", Today, Actor, Now);

        Assert.Equal(BillKind.Charge, bill.Kind);
        Assert.Null(bill.MeterId);
        Assert.Null(bill.MeterReadingId);
        Assert.Null(bill.MeterNumber);
        Assert.Null(bill.RatePlanId);
        Assert.Null(bill.RatePlanCode);
        Assert.Null(bill.RatePlanName);
        Assert.Null(bill.RatePlanEffectiveFrom);
        Assert.Null(bill.UnitOfMeasure);
        Assert.Null(bill.CycleCode);
        Assert.Equal(0m, bill.Consumption);
    }

    [Fact]
    public void A_charge_bill_covers_the_day_it_was_raised_on_both_sides()
    {
        // A charge bill covers no span of supply. A zero-length period stated honestly beats a
        // made-up month: Finance posts against these dates and a statement orders by them.
        var bill = Bill.ForCharges("BIL-000002", Account(), [Fee("Reconnection fee", 60.00m)], "USD", Today, Actor, Now);

        Assert.Equal(Today, bill.PeriodStart);
        Assert.Equal(Today, bill.PeriodEnd);
    }

    [Fact]
    public void A_charge_bill_is_fees_all_the_way_down()
    {
        var bill = Bill.ForCharges(
            "BIL-000002",
            Account(),
            [Fee("Reconnection fee", 60.00m), Fee("Meter test fee", 75.00m)],
            "USD",
            Today,
            Actor,
            Now);

        Assert.Equal(135.00m, bill.TotalAmount);
        Assert.Equal(bill.TotalAmount, bill.FeeAmount);
        Assert.All(bill.Lines, line => Assert.Equal(ChargeKind.Fee, line.Kind));
        Assert.Equal([1, 2], bill.Lines.Select(line => line.Sequence));
    }

    [Fact]
    public void A_charge_bill_starts_as_a_draft_like_every_other_bill() =>
        Assert.Equal(
            BillStatus.Draft,
            Bill.ForCharges("BIL-000002", Account(), [Fee("Reconnection fee", 60.00m)], "USD", Today, Actor, Now).Status);

    [Fact]
    public void A_charge_bill_for_nothing_is_refused()
    {
        // A bill raised for no charges is a document nobody can pay.
        var refusal = Assert.Throws<BillingValidationException>(() =>
            Bill.ForCharges("BIL-000002", Account(), [], "USD", Today, Actor, Now));

        Assert.Contains("carries no charges", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_charge_bill_without_a_currency_is_refused() =>
        Assert.Throws<BillingValidationException>(() =>
            Bill.ForCharges("BIL-000002", Account(), [Fee("Reconnection fee", 60.00m)], "  ", Today, Actor, Now));

    [Fact]
    public void A_charge_bill_is_adjusted_like_any_other_once_it_is_issued()
    {
        // Correcting a fee the customer has been sent is an adjustment to the bill, which is exactly
        // why AccountChargeStatus.Billed is terminal.
        var bill = Bill.ForCharges("BIL-000002", Account(), [Fee("Reconnection fee", 60.00m)], "USD", Today, Actor, Now);

        bill.Issue(Today, Today.AddDays(21), Actor, Now);
        bill.Adjust(BillAdjustmentKind.Credit, 60.00m, "Fee waived after review.", Actor, Now.AddMinutes(1));

        Assert.Equal(0m, bill.AmountDue);

        // The printed total keeps saying what the document said — WP-2.4's rule, unchanged by fees.
        Assert.Equal(60.00m, bill.TotalAmount);
        Assert.Equal(60.00m, bill.FeeAmount);
    }
}
