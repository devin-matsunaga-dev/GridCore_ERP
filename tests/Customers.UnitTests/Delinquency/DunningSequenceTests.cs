using GridCore.Modules.Customers.Features.Delinquency;
using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.UnitTests.Delinquency;

/// <summary>
/// The shipped dunning sequence (WP-2.19): reference data, complete, in order, and honest about
/// being a demo. Pure — the rows are built by the same static list the migration seeds from.
/// </summary>
public class DunningSequenceTests
{
    [Fact]
    public void Every_declared_notice_has_a_published_step() =>
        // The check that runs where the model is built, so a declared notice with no row fails at
        // startup rather than at the desk — DepositRules and FeeSchedules established the shape.
        DunningSequence.RequireComplete(DunningSequence.All);

    [Fact]
    public void A_missing_step_fails_the_completeness_check()
    {
        var without = DunningSequence.All.Where(step => step.NoticeType != DunningNoticeType.Disconnection).ToList();

        var refusal = Assert.Throws<RegistryValidationException>(() => DunningSequence.RequireComplete(without));

        Assert.Contains("Disconnection", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("migration", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sequence_with_a_gap_in_its_numbering_fails_the_completeness_check()
    {
        // Two step 2s has no answer to "what comes next", and DueOn would pick whichever the
        // enumeration yielded last.
        var misnumbered = new[]
        {
            Step(DunningNoticeType.Reminder, sequence: 1),
            Step(DunningNoticeType.Delinquency, sequence: 2),
            Step(DunningNoticeType.Disconnection, sequence: 4),
        };

        Assert.Throws<RegistryValidationException>(() => DunningSequence.RequireComplete(misnumbered));
    }

    [Fact]
    public void The_steps_are_served_in_order_and_get_later_and_dearer_as_they_go()
    {
        var steps = DunningSequence.All.OrderBy(step => step.Sequence).ToList();

        Assert.Equal(
            [DunningNoticeType.Reminder, DunningNoticeType.Delinquency, DunningNoticeType.Disconnection],
            steps.Select(step => step.NoticeType));

        // A sequence whose second letter went out sooner than its first would be a sequence in name
        // only.
        Assert.True(steps[0].DaysPastDue < steps[1].DaysPastDue);
        Assert.True(steps[1].DaysPastDue < steps[2].DaysPastDue);
        Assert.True(steps[0].MinimumArrears <= steps[1].MinimumArrears);
        Assert.True(steps[1].MinimumArrears <= steps[2].MinimumArrears);
    }

    [Fact]
    public void Only_the_disconnection_notice_starts_a_clock() =>
        // A reminder warns of nothing, so there is nothing to wait out. The waiting period is what
        // makes the disconnection notice the one that matters legally.
        Assert.All(
            DunningSequence.All,
            step => Assert.Equal(step.NoticeType is DunningNoticeType.Disconnection, step.HasWaitingPeriod));

    [Fact]
    public void Every_shipped_message_says_it_is_a_demo() =>
        // The provenance WORK_PACKAGES.md asks for, in the row itself: CUC's publications disagree
        // with each other and change without notice, so nobody reading "ten days" off a screen
        // should be able to mistake it for a statutory certainty.
        Assert.All(
            DunningSequence.All,
            step => Assert.Contains("not an authoritative notice", step.Message, StringComparison.Ordinal));

    [Fact]
    public void The_disconnection_message_says_the_deposit_will_be_applied_first() =>
        // The notice has to tell the customer what the statute obliges the utility to do, or the
        // record of having served it proves less than it should.
        Assert.Contains(
            "security deposit held will be applied",
            DunningSequence.For(DunningNoticeType.Disconnection)!.Message,
            StringComparison.Ordinal);

    [Theory]
    [InlineData(9, 100.00, false)]
    [InlineData(10, 100.00, true)]
    [InlineData(60, 9.99, false)]
    [InlineData(60, 10.00, true)]
    public void A_step_falls_due_only_when_the_account_is_both_late_enough_and_behind_enough(
        int daysPastDue,
        decimal pastDue,
        bool isDue) =>
        // Both conditions, because a notice served on a customer forty days behind with $2 owing is
        // a notice that costs more to post than it collects.
        Assert.Equal(isDue, DunningSequence.For(DunningNoticeType.Reminder)!.IsDue(daysPastDue, pastDue));

    [Fact]
    public void An_account_owing_nothing_has_reached_no_step_however_long_ago_that_was() =>
        Assert.Null(DunningSequence.DueOn(DunningSequence.All, daysPastDue: 400, pastDueAmount: 0m));

    [Fact]
    public void The_step_due_is_the_FURTHEST_reached_rather_than_the_next_unserved()
    {
        // A queue that offered the reminder would have the desk send a courtesy letter to somebody
        // who should be receiving a disconnection notice.
        var due = DunningSequence.DueOn(DunningSequence.All, daysPastDue: 46, pastDueAmount: 200.00m);

        Assert.Equal(DunningNoticeType.Disconnection, due!.NoticeType);
    }

    [Fact]
    public void An_account_barely_late_has_reached_only_the_reminder()
    {
        var due = DunningSequence.DueOn(DunningSequence.All, daysPastDue: 12, pastDueAmount: 200.00m);

        Assert.Equal(DunningNoticeType.Reminder, due!.NoticeType);
    }

    [Fact]
    public void A_published_threshold_is_a_whole_number_of_cents() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DunningStep.Reference(
            DunningNoticeType.Reminder,
            sequence: 1,
            daysPastDue: 10,
            minimumArrears: 10.005m,
            waitingPeriodDays: 0,
            "USD",
            "Payment reminder",
            "A demo notice."));

    private static DunningStep Step(DunningNoticeType noticeType, int sequence) =>
        DunningStep.Reference(
            noticeType,
            sequence,
            daysPastDue: sequence * 10,
            minimumArrears: 10.00m,
            waitingPeriodDays: 0,
            "USD",
            noticeType.ToString(),
            "A demo notice.");
}
