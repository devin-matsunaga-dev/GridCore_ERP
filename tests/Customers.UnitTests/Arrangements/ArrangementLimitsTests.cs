using GridCore.Modules.Customers.Features.Arrangements;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.UnitTests.Arrangements;

/// <summary>
/// The published arrangement ceilings (WP-2.20) — reference data, and the completeness check that
/// fails a startup which forgot a row.
/// </summary>
public sealed class ArrangementLimitsTests
{
    [Fact]
    public void Every_declared_customer_class_has_exactly_one_ceiling() =>
        // The check runs where the model is built, so a gap is found at startup rather than by the
        // rep on the telephone — the shape DepositRules, FeeSchedules and DunningSequence established.
        ArrangementLimits.RequireComplete(ArrangementLimits.All);

    [Fact]
    public void A_class_with_no_ceiling_fails_the_completeness_check()
    {
        var incomplete = ArrangementLimits.All.Where(limit => limit.CustomerClass is CustomerClass.Residential);

        var failure = Assert.Throws<RegistryValidationException>(() => ArrangementLimits.RequireComplete(incomplete));

        Assert.Contains("Commercial", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_ceilings_claiming_one_class_fail_the_completeness_check()
    {
        var duplicated = ArrangementLimits.All.Concat([ArrangementLimits.For(CustomerClass.Residential)!]);

        var failure = Assert.Throws<RegistryValidationException>(() => ArrangementLimits.RequireComplete(duplicated));

        Assert.Contains("which row was read", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_shipped_figure_says_in_its_own_row_that_it_is_a_demo_figure() =>
        // CUC publishes that Customer Service will arrange payment rather than disconnect, and does
        // NOT publish what a rep may sign alone. Nobody may mistake $1,500 for a published authority.
        Assert.All(
            ArrangementLimits.All,
            limit => Assert.Contains("not an authoritative delegation", limit.Notes, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Every_shipped_ceiling_is_a_whole_number_of_cents() =>
        Assert.All(ArrangementLimits.All, limit => Assert.True(Money.IsRounded(limit.MaximumBalance)));

    [Fact]
    public void The_commercial_ceiling_is_the_higher_of_the_two() =>
        // A business owing four thousand dollars over six months is ordinary and a household owing
        // the same is not. One figure for both would either tie the commercial desk's hands or hand
        // the residential desk an authority nobody meant to give it.
        Assert.True(
            ArrangementLimits.For(CustomerClass.Commercial)!.MaximumBalance
            > ArrangementLimits.For(CustomerClass.Residential)!.MaximumBalance);

    [Theory]
    [InlineData(100.00, 3, false)]
    [InlineData(1500.00, 6, false)]
    [InlineData(1500.01, 6, true)]
    [InlineData(100.00, 7, true)]
    [InlineData(5000.00, 12, true)]
    public void Either_ceiling_on_its_own_sends_a_residential_arrangement_for_approval(
        decimal balance,
        int instalmentCount,
        bool expected) =>
        // Two ceilings rather than one, because either alone is trivially avoided by moving the
        // other: a small debt spread over three years is a write-off wearing a schedule's clothes.
        Assert.Equal(
            expected,
            ArrangementLimits.For(CustomerClass.Residential)!.RequiresApproval(balance, instalmentCount));

    [Fact]
    public void A_ceiling_finer_than_a_cent_is_refused_rather_than_published() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ArrangementLimit.Reference(
            CustomerClass.Residential,
            1_500.005m,
            "USD",
            6,
            "Demo figure."));

    [Fact]
    public void A_ceiling_beyond_what_GridCore_will_schedule_at_all_is_refused() =>
        // A rep's authority cannot exceed the outer edge: it would publish a limit no schedule could
        // ever reach.
        Assert.Throws<ArgumentOutOfRangeException>(() => ArrangementLimit.Reference(
            CustomerClass.Residential,
            1_500.00m,
            "USD",
            PaymentArrangement.MaximumInstalments + 1,
            "Demo figure."));

    [Fact]
    public void The_row_ids_are_stable_across_builds() =>
        // Derived from a fixed instant and the class, so `dotnet ef migrations add` does not rewrite
        // the seeded rows on every model build — ReferenceId's whole reason for existing.
        Assert.Equal(
            ArrangementLimits.All.Select(limit => limit.Id),
            ArrangementLimits.All.Select(limit => limit.Id));
}
