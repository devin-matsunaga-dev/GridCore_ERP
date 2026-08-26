using GridCore.Contracts.Services;
using GridCore.Modules.Billing.Features.Fees;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Billing.UnitTests.Fees;

/// <summary>
/// The shipped fee schedule: reference data, effective-dated, and complete. Pure — no database, no
/// host — because the list a migration is generated from is a static list, and holding it to its own
/// rules here is what stops a gap reaching a counter (CONVENTIONS.md rule C).
/// </summary>
public class FeeSchedulesTests
{
    [Fact]
    public void Every_declared_fee_has_a_published_row()
    {
        // The completeness check WORK_PACKAGES.md asks for, run against the shipped list. Adding a
        // FeeCode member without adding its row in the same migration fails here — and at startup,
        // because FeeScheduleConfiguration calls the same method while building the model.
        FeeSchedules.RequireComplete(FeeSchedules.All);

        Assert.All(
            Enum.GetValues<FeeCode>(),
            code => Assert.NotEmpty(FeeSchedules.VersionsOf(code)));
    }

    [Fact]
    public void The_completeness_check_fails_when_a_declared_code_has_no_row()
    {
        // THE FAILURE PATH. A schedule missing one fee is refused loudly, naming the fee — the whole
        // point of declaring the codes as an enum rather than as free text.
        var withoutReconnections = FeeSchedules.All.Where(entry => entry.Code != FeeCode.Reconnection).ToList();

        var refusal = Assert.Throws<BillingValidationException>(() => FeeSchedules.RequireComplete(withoutReconnections));

        Assert.Contains(nameof(FeeCode.Reconnection), refusal.Message, StringComparison.Ordinal);
        Assert.Contains("migration", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_completeness_check_fails_when_two_rows_claim_one_fee_on_one_day()
    {
        // A schedule with two figures in force on the same day has no answer to "what does this
        // cost". The unique index refuses the pair in the database; this refuses it in the list the
        // migration is generated from, which is where somebody would introduce it.
        var doubled = FeeSchedules.All
            .Append(FeeScheduleEntry.Reference(
                FeeCode.Inspection,
                "Installation inspection fee",
                ServiceType.Electricity,
                75.00m,
                FeeSchedules.Currency,
                FeeSchedules.OriginalEffectiveFrom,
                "A second figure for a day that already has one."))
            .ToList();

        var refusal = Assert.Throws<BillingValidationException>(() => FeeSchedules.RequireComplete(doubled));

        Assert.Contains(nameof(FeeCode.Inspection), refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repriced_fee_is_a_new_row_and_the_old_one_still_says_what_it_said()
    {
        // A SCHEDULE ROW IS NEVER EDITED IN PLACE. The reconnection fee ships twice: two rows, two
        // ids, two effective dates, two figures — which is what lets a charge raised in June still
        // report June's figure after July's row exists.
        var versions = FeeSchedules.VersionsOf(FeeCode.Reconnection);

        Assert.Equal(2, versions.Count);
        Assert.Equal([FeeSchedules.OriginalEffectiveFrom, FeeSchedules.ReconnectionRevisionFrom], versions.Select(entry => entry.EffectiveFrom));
        Assert.Distinct(versions.Select(entry => entry.Id));
        Assert.True(versions[1].Amount > versions[0].Amount);
    }

    [Theory]
    [InlineData(2025, 1, 1, 50.00)]
    [InlineData(2026, 6, 30, 50.00)]
    [InlineData(2026, 7, 1, 60.00)]
    [InlineData(2030, 1, 1, 60.00)]
    public void A_fee_is_priced_by_the_version_in_force_on_the_day(int year, int month, int day, decimal expected) =>
        // Either side of the repricing, including the boundary day itself: a version takes effect ON
        // its effective date, not after it. The same rule RatePlanSelector holds for a tariff.
        Assert.Equal(expected, FeeSchedules.InForceOn(FeeCode.Reconnection, new DateOnly(year, month, day))!.Amount);

    [Fact]
    public void A_fee_that_had_not_been_published_yet_prices_to_nothing() =>
        // A null is a real answer, not an omission: a fee raised before it was published has no
        // figure, and saying so beats charging one nobody had announced.
        Assert.Null(FeeSchedules.InForceOn(FeeCode.Reconnection, FeeSchedules.OriginalEffectiveFrom.AddDays(-1)));

    [Fact]
    public void Every_shipped_figure_is_a_positive_whole_number_of_cents() =>
        // Flat rows only. WP-2.19's late charge publishes a RATE and no amount, so asking it for a
        // whole number of cents would be asking the wrong question of the one row in the catalogue
        // that has no figure until something is charged on it.
        Assert.All(
            FeeSchedules.All.Where(entry => entry.Basis is FeeBasis.Flat),
            entry =>
            {
                Assert.NotNull(entry.Amount);
                Assert.True(entry.Amount > Money.Zero);
                Assert.True(Money.IsRounded(entry.Amount.Value));
            });

    [Fact]
    public void Every_shipped_figure_says_it_is_a_demo_figure() =>
        // The provenance WORK_PACKAGES.md asks for, in the row itself: CUC's own publications
        // disagree on amounts and change without notice, so nobody reading $135 off a screen should
        // be able to mistake it for an authoritative charge.
        Assert.All(
            FeeSchedules.All,
            entry => Assert.Contains("Demo figure", entry.Description, StringComparison.Ordinal));

    [Fact]
    public void Every_row_carries_a_currency_and_a_service()
    {
        Assert.All(FeeSchedules.All, entry => Assert.Equal(FeeSchedules.Currency, entry.Currency));

        // One service today, because the utility bills one. WP-2.17 is what re-keys this on
        // (class × service type) and makes a water fee expressible.
        Assert.All(FeeSchedules.All, entry => Assert.Equal(ServiceType.Electricity, entry.ServiceType));
    }

    [Fact]
    public void Ids_are_derived_from_the_code_and_the_effective_date()
    {
        // The migration seeds the same rows every time it is generated, and a repricing is a new row
        // rather than a collision with the old one — which is exactly what the version key buys.
        Assert.Distinct(FeeSchedules.All.Select(entry => entry.Id));

        Assert.All(
            FeeSchedules.All,
            entry => Assert.Equal(entry.Code + "@" + entry.EffectiveFrom.ToString("yyyy-MM-dd"), entry.VersionKey));
    }

    [Fact]
    public void A_row_priced_below_a_cent_is_refused() =>
        // Refused rather than rounded, the rule Money states: a schedule figure finer than a cent is
        // a typo in reference data, and rounding it would put a figure on a bill that no published
        // document says.
        Assert.Throws<ArgumentOutOfRangeException>(() => FeeScheduleEntry.Reference(
            FeeCode.MeterTest,
            "Meter test fee",
            ServiceType.Electricity,
            75.005m,
            FeeSchedules.Currency,
            FeeSchedules.OriginalEffectiveFrom,
            "Finer than a cent."));

    [Fact]
    public void The_selector_ignores_versions_of_other_fees() =>
        // A caller holding the whole schedule must not be handed an inspection fee because it
        // happened to be published later — word for word the trap RatePlanSelector documents.
        Assert.Equal(
            FeeCode.MeterTest,
            FeeScheduleSelector.InForceOn(FeeSchedules.All, FeeCode.MeterTest, new DateOnly(2030, 1, 1))!.Code);

    [Fact]
    public void The_late_charge_is_published_as_a_rate_and_has_no_amount()
    {
        // WP-2.19's one rate row. A flat late charge would ask the same of a customer $40 behind as
        // of one $4,000 behind, which is why the legislature expresses it as a percentage — and why
        // this row carries a rate and, deliberately, nothing in the amount column.
        var row = Assert.Single(FeeSchedules.VersionsOf(FeeCode.LateCharge));

        Assert.Equal(FeeBasis.Rate, row.Basis);
        Assert.Equal(FeeSchedules.LateChargeMonthlyRate, row.Rate);
        Assert.Null(row.Amount);
    }

    [Fact]
    public void Every_other_shipped_fee_is_flat_and_carries_no_rate() =>
        Assert.All(
            FeeSchedules.All.Where(entry => entry.Code != FeeCode.LateCharge),
            entry =>
            {
                Assert.Equal(FeeBasis.Flat, entry.Basis);
                Assert.Null(entry.Rate);
            });

    [Fact]
    public void A_rate_row_prices_a_basis_to_the_cent()
    {
        // Rounded at the figure, half away from zero — Money's rule, and the one a customer checking
        // the arithmetic on the back of an envelope gets the same answer from.
        var row = FeeSchedules.InForceOn(FeeCode.LateCharge, FeeSchedules.OriginalEffectiveFrom)!;

        Assert.Equal(2.00m, row.PriceOn(200.00m));
        Assert.Equal(2.35m, row.PriceOn(234.56m));
        Assert.Equal(0.40m, row.PriceOn(40.00m));
    }

    [Fact]
    public void A_flat_row_refuses_to_be_priced_on_a_basis() =>
        Assert.Throws<BillingValidationException>(() =>
            FeeSchedules.InForceOn(FeeCode.Reconnection, FeeSchedules.OriginalEffectiveFrom)!.PriceOn(200.00m));

    [Fact]
    public void A_published_rate_must_be_positive_and_no_finer_than_the_column()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Rate(0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rate(-0.01m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rate(0.012345m));

        static FeeScheduleEntry Rate(decimal rate) => FeeScheduleEntry.ReferenceRate(
            FeeCode.LateCharge,
            "Late payment charge",
            ServiceType.Electricity,
            rate,
            FeeSchedules.Currency,
            FeeSchedules.OriginalEffectiveFrom,
            "A rate row.");
    }
}
