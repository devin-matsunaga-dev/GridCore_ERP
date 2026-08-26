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
        Assert.All(
            FeeSchedules.All,
            entry =>
            {
                Assert.True(entry.Amount > Money.Zero);
                Assert.True(Money.IsRounded(entry.Amount));
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
}
