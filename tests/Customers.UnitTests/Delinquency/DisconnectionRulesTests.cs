using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Features.Delinquency;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.UnitTests.Delinquency;

/// <summary>
/// The four disconnection tests and the statutory deposit offset (WP-2.19), decided as a pure
/// function of five figures and two dates — which is why every case WORK_PACKAGES.md asks for is a
/// fast test with no database, no bill and no customer anywhere near it.
/// </summary>
public class DisconnectionRulesTests
{
    private static readonly Guid Account = Guid.CreateVersion7();
    private static readonly DateOnly AsOf = new(2026, 9, 1);
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly RegistryActor Clerk = new("auth0|clerk", "A customer service rep");

    /// <summary>The shipped disconnection step: $50 past due, 45 days late, ten days to wait.</summary>
    private static DunningStep Step => DunningSequence.For(DunningNoticeType.Disconnection)!;

    private static AccountArrears Arrears(decimal pastDue, int daysPastDue = 60) =>
        new(
            Account,
            "USD",
            AsOf,
            OutstandingAmount: pastDue,
            PastDueAmount: pastDue,
            CurrentAmount: 0m,
            OldestDueDate: AsOf.AddDays(-daysPastDue),
            daysPastDue,
            Buckets: [],
            Bills: []);

    private static DunningNotice Served(DateOnly servedOn, decimal arrears = 200.00m) =>
        DunningNotice.Serve(
            Step,
            Account,
            "A-000001",
            Guid.CreateVersion7(),
            "Rita Sablan",
            servedOn,
            arrears,
            "USD",
            daysPastDue: 60,
            notes: null,
            Clerk,
            Now);

    private static DisconnectionEligibility Decide(
        decimal pastDue,
        decimal depositHeld,
        DunningNotice? notice,
        PaymentArrangementStanding? arrangement = null) =>
        DisconnectionRules.Decide(Arrears(pastDue), depositHeld, Step, notice, arrangement, isOffsetApplied: false);

    [Fact]
    public void A_300_deposit_against_200_arrears_offsets_exactly_200_and_leaves_the_account_ineligible()
    {
        // THE STATUTE. CNMI Public Law 16-17 obliges the utility to set the deposit against
        // qualifying past-due amounts BEFORE disconnection, so a customer whose deposit clears their
        // debt is not eligible at all — not "eligible but we chose not to".
        var eligibility = Decide(pastDue: 200.00m, depositHeld: 300.00m, Served(AsOf.AddDays(-20)));

        Assert.Equal(200.00m, eligibility.OffsetAmount);
        Assert.Equal(0m, eligibility.ArrearsAfterOffset);
        Assert.Equal(100.00m, eligibility.DepositHeldAfterOffset);
        Assert.True(eligibility.DepositClearsArrears);
        Assert.False(eligibility.IsEligible);

        // And it fails on the ARREARS test, not on a notice or a clock: those both passed.
        Assert.Equal([DisconnectionRules.ArrearsTest], eligibility.Blockers);
    }

    [Fact]
    public void A_100_deposit_against_200_arrears_offsets_100_and_leaves_it_eligible()
    {
        // The other half of the same rule. $100 remains, the published threshold is $50, the notice
        // was served three weeks ago and its ten days have run out.
        var eligibility = Decide(pastDue: 200.00m, depositHeld: 100.00m, Served(AsOf.AddDays(-20)));

        Assert.Equal(100.00m, eligibility.OffsetAmount);
        Assert.Equal(100.00m, eligibility.ArrearsAfterOffset);
        Assert.Equal(0m, eligibility.DepositHeldAfterOffset);
        Assert.False(eligibility.DepositClearsArrears);
        Assert.True(eligibility.IsEligible);
        Assert.Empty(eligibility.Blockers);
    }

    [Fact]
    public void The_offset_is_never_more_than_is_held_and_never_more_than_is_owed()
    {
        // More than is owed would leave a credit on a bill with nowhere to sit; more than is held
        // would have the utility hand over money it is not holding.
        Assert.Equal(0m, DisconnectionRules.QualifyingOffset(depositHeld: 0m, pastDueAmount: 200.00m));
        Assert.Equal(200.00m, DisconnectionRules.QualifyingOffset(depositHeld: 500.00m, pastDueAmount: 200.00m));
        Assert.Equal(75.00m, DisconnectionRules.QualifyingOffset(depositHeld: 75.00m, pastDueAmount: 200.00m));

        // Floored, so an account somehow holding nothing against nothing offsets nothing rather than
        // producing a negative movement no ledger could take.
        Assert.Equal(0m, DisconnectionRules.QualifyingOffset(depositHeld: 0m, pastDueAmount: 0m));
    }

    [Fact]
    public void Eligibility_is_false_when_the_notice_was_never_served()
    {
        var eligibility = Decide(pastDue: 200.00m, depositHeld: 0m, notice: null);

        Assert.False(eligibility.IsEligible);
        Assert.Contains(DisconnectionRules.NoticeTest, eligibility.Blockers);

        // And the waiting period fails too, because nothing started it. Both are reported, so a rep
        // is told what actually has to happen rather than one thing at a time.
        Assert.Contains(DisconnectionRules.WaitingPeriodTest, eligibility.Blockers);
        Assert.Null(eligibility.DisconnectionNoticeServedOn);
        Assert.Null(eligibility.EligibleFrom);
    }

    [Fact]
    public void Eligibility_is_false_again_inside_the_waiting_period()
    {
        // Served yesterday; the published period is ten days. This is the difference between a
        // utility that gave a customer a chance to pay and one that posted a letter on the way to
        // the van.
        var eligibility = Decide(pastDue: 200.00m, depositHeld: 0m, Served(AsOf.AddDays(-1)));

        Assert.False(eligibility.IsEligible);
        Assert.Equal([DisconnectionRules.WaitingPeriodTest], eligibility.Blockers);
        Assert.Equal(AsOf.AddDays(-1).AddDays(Step.WaitingPeriodDays), eligibility.EligibleFrom);
    }

    [Fact]
    public void The_waiting_period_elapses_on_its_last_day_and_not_the_day_after()
    {
        // Boundary, stated on purpose: "within ten days of the date of this notice" means the tenth
        // day is the first day the utility may act, and an off-by-one here is a day of supply.
        var served = AsOf.AddDays(-Step.WaitingPeriodDays);

        Assert.True(Decide(200.00m, 0m, Served(served)).IsEligible);
        Assert.False(Decide(200.00m, 0m, Served(served.AddDays(1))).IsEligible);
    }

    [Fact]
    public void Eligibility_is_false_below_the_published_threshold()
    {
        // $40 past due against a $50 threshold. Cutting somebody off over less than the utility
        // publishes as worth cutting off over is what the figure exists to prevent.
        var eligibility = Decide(pastDue: 40.00m, depositHeld: 0m, Served(AsOf.AddDays(-20)));

        Assert.False(eligibility.IsEligible);
        Assert.Equal([DisconnectionRules.ArrearsTest], eligibility.Blockers);
        Assert.Equal(Step.MinimumArrears, eligibility.Threshold);
    }

    [Fact]
    public void An_account_at_the_threshold_exactly_is_eligible() =>
        // A floor, never a ceiling — the same asymmetry DepositRule.Assess states.
        Assert.True(Decide(Step.MinimumArrears, 0m, Served(AsOf.AddDays(-20))).IsEligible);

    [Fact]
    public void An_account_owing_nothing_is_never_eligible_however_much_was_served_on_it()
    {
        var eligibility = Decide(pastDue: 0m, depositHeld: 0m, Served(AsOf.AddDays(-20)));

        Assert.False(eligibility.IsEligible);
        Assert.Equal([DisconnectionRules.ArrearsTest], eligibility.Blockers);
        Assert.Equal(0m, eligibility.OffsetAmount);
    }

    [Fact]
    public void An_arrangement_that_suppresses_disconnection_makes_the_account_ineligible()
    {
        // The fourth test, and the reason WP-2.20 matters to anything outside itself.
        var arrangement = new PaymentArrangementStanding(Account, "Active", SuppressesDisconnection: true);

        var eligibility = Decide(200.00m, 0m, Served(AsOf.AddDays(-20)), arrangement);

        Assert.False(eligibility.IsEligible);
        Assert.Equal([DisconnectionRules.ArrangementTest], eligibility.Blockers);
    }

    [Fact]
    public void A_broken_arrangement_protects_nobody()
    {
        // "Broken restores disconnection eligibility" — WP-2.20's words, and the reason the seam
        // answers with a flag the owner of arrangements sets rather than a status this file reads.
        var arrangement = new PaymentArrangementStanding(Account, "Broken", SuppressesDisconnection: false);

        Assert.True(Decide(200.00m, 0m, Served(AsOf.AddDays(-20)), arrangement).IsEligible);
    }

    [Fact]
    public void Every_test_carries_the_figures_behind_its_answer()
    {
        // A screen should never have to restate the arithmetic, and a rep reading a refusal should
        // be able to tell the customer what would change it.
        var eligibility = Decide(200.00m, 300.00m, Served(AsOf.AddDays(-20)));

        Assert.All(eligibility.Tests, test => Assert.False(string.IsNullOrWhiteSpace(test.Detail)));

        var arrears = Assert.Single(eligibility.Tests, test => test.Name == DisconnectionRules.ArrearsTest);

        Assert.Contains("200.00", arrears.Detail, StringComparison.Ordinal);
        Assert.Contains("50.00", arrears.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_read_says_the_offset_has_not_been_made()
    {
        // The split the whole design turns on: GetAsync computes the figures, EvaluateAsync makes
        // them so. A screen showing what would happen must not be a screen that makes it happen.
        var eligibility = Decide(200.00m, 300.00m, Served(AsOf.AddDays(-20)));

        Assert.False(eligibility.IsOffsetApplied);
    }

    [Fact]
    public void The_statutory_reason_names_the_law_and_the_bill_it_settled()
    {
        // WORK_PACKAGES.md: "a legally obliged movement should defend itself from the trail without
        // anyone remembering why it happened".
        var reason = StatutoryBasis.OffsetReason("BIL-000042");

        Assert.Contains("CNMI Public Law 16-17", reason, StringComparison.Ordinal);
        Assert.Contains("BIL-000042", reason, StringComparison.Ordinal);
    }
}
