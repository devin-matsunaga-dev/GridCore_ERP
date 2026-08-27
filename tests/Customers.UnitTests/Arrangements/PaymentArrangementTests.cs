using GridCore.Modules.Customers.Features.Arrangements;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.UnitTests.Arrangements;

/// <summary>
/// The arrangement aggregate and its state machine (WP-2.20) — allocation, breaking, keeping, and
/// the computed standing an account's protection is read from. Pure: the aggregate is built by hand,
/// so none of this needs a database.
/// </summary>
public sealed class PaymentArrangementTests
{
    private static readonly DateOnly Made = new(2026, 8, 27);
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    /// <summary>An arrangement over <paramref name="balance"/>, built through the real factory.</summary>
    private static PaymentArrangement An(
        decimal balance = 300.00m,
        decimal downPayment = 0m,
        int instalmentCount = 3,
        ArrangementLimit? limit = null,
        Guid? approvalRequestId = null)
    {
        var governing = limit ?? ArrangementLimits.For(CustomerClass.Residential)!;
        var account = ArrangementFixtures.Account();
        var customer = ArrangementFixtures.Customer();

        return PaymentArrangement.Propose(
            "PA-000001",
            account,
            customer,
            balance,
            "USD",
            downPayment,
            instalmentCount,
            ArrangementSchedule.DefaultIntervalDays,
            Made,
            governing,
            approvalRequestId,
            ArrangementSchedule.Build(
                balance,
                downPayment,
                instalmentCount,
                Made,
                Made.AddDays(ArrangementSchedule.DefaultIntervalDays)),
            notes: null,
            RegistryActor.Of(new FakeCurrentUser("auth0|cs-agent", "Ana Cruz")),
            Now);
    }

    [Fact]
    public void A_proposal_starts_proposed_and_protects_nobody()
    {
        // A proposal is not a promise: the customer has not agreed, and above the rep's limit nobody
        // has approved it. Protection begins at Active and nowhere earlier.
        var arrangement = An();

        Assert.Equal(PaymentArrangementStatus.Proposed, arrangement.Status);
        Assert.False(arrangement.SuppressesDisconnectionOn(Made));
    }

    [Fact]
    public void The_schedule_adds_up_to_what_was_arranged()
    {
        var arrangement = An(balance: 100.00m, instalmentCount: 3);

        Assert.Equal(100.00m, arrangement.ScheduledAmount);
        Assert.Equal(arrangement.ArrearsBalance, arrangement.ScheduledAmount);
        Assert.Equal(100.00m, arrangement.OutstandingAmount);
    }

    [Fact]
    public void An_active_arrangement_suppresses_disconnection()
    {
        var arrangement = An();
        arrangement.Activate(Made);

        Assert.Equal(PaymentArrangementStatus.Active, arrangement.Status);
        Assert.True(arrangement.SuppressesDisconnectionOn(Made));
    }

    [Fact]
    public void A_payment_applies_to_the_earliest_unpaid_instalment()
    {
        // WORK_PACKAGES.md's wording exactly.
        var arrangement = An(balance: 300.00m, instalmentCount: 3);
        arrangement.Activate(Made);

        var applied = arrangement.Apply(100.00m, Now);

        var line = Assert.Single(applied);
        Assert.Equal(1, line.Instalment.Sequence);
        Assert.Equal(100.00m, line.Applied);
        Assert.Equal(200.00m, arrangement.OutstandingAmount);
        Assert.Equal(2, arrangement.NextInstalment!.Sequence);
    }

    [Fact]
    public void A_payment_larger_than_one_instalment_cascades_down_the_schedule()
    {
        // A customer who pays two months at once has paid two months. The alternative would leave a
        // credit sitting on a line that is already settled.
        var arrangement = An(balance: 300.00m, instalmentCount: 3);
        arrangement.Activate(Made);

        var applied = arrangement.Apply(250.00m, Now);

        Assert.Equal([1, 2, 3], applied.Select(line => line.Instalment.Sequence));
        Assert.Equal([100.00m, 100.00m, 50.00m], applied.Select(line => line.Applied));
        Assert.Equal(50.00m, arrangement.OutstandingAmount);
    }

    [Fact]
    public void A_payment_beyond_the_whole_schedule_takes_only_what_the_schedule_is_owed()
    {
        // An arrangement is a promise about receivables that already exist. Money over the promise
        // is the bills' business, and Billing's own consumer of the same payment has it.
        var arrangement = An(balance: 300.00m, instalmentCount: 3);
        arrangement.Activate(Made);

        var applied = arrangement.Apply(500.00m, Now);

        Assert.Equal(300.00m, applied.Sum(line => line.Applied));
        Assert.Equal(0m, arrangement.OutstandingAmount);
        Assert.Equal(300.00m, arrangement.PaidAmount);
    }

    [Fact]
    public void One_missed_due_date_breaks_it()
    {
        // WORK_PACKAGES.md's wording. Nothing else is required — not two, not a grace period.
        var arrangement = An(balance: 300.00m, instalmentCount: 3);
        arrangement.Activate(Made);

        var firstDue = arrangement.Instalments.First().DueDate;

        Assert.Equal(PaymentArrangementStatus.Active, arrangement.StandingOn(firstDue));
        Assert.Equal(PaymentArrangementStatus.Broken, arrangement.StandingOn(firstDue.AddDays(1)));
        Assert.False(arrangement.SuppressesDisconnectionOn(firstDue.AddDays(1)));
    }

    [Fact]
    public void Paying_on_the_due_date_itself_is_paying_on_time()
    {
        // An arrangement that broke at one minute past midnight on its own due date would break
        // every arrangement ever made.
        var arrangement = An(balance: 300.00m, instalmentCount: 3);
        arrangement.Activate(Made);

        var firstDue = arrangement.Instalments.First().DueDate;

        Assert.False(arrangement.Instalments.First().IsMissedBy(firstDue));
        Assert.True(arrangement.Instalments.First().IsMissedBy(firstDue.AddDays(1)));
    }

    [Fact]
    public void A_fully_settled_arrangement_stands_as_kept_even_where_a_date_slipped()
    {
        // Settled is checked before missed, deliberately: the utility got its money on a promise the
        // customer honoured, and calling that broken would be a book-keeping opinion rather than a
        // fact about the account.
        var arrangement = An(balance: 300.00m, instalmentCount: 3);
        arrangement.Activate(Made);
        arrangement.Apply(300.00m, Now);

        Assert.Equal(PaymentArrangementStatus.Kept, arrangement.StandingOn(Made.AddYears(1)));
    }

    [Fact]
    public void A_broken_arrangement_cannot_be_resumed_only_replaced()
    {
        // WORK_PACKAGES.md's wording, and the package's sharpest rule: the record of the broken
        // promise stays, and a fresh promise is a fresh row somebody had to decide to make.
        var arrangement = An();
        arrangement.Activate(Made);
        arrangement.Break(Made.AddDays(40));

        var failure = Assert.Throws<RegistryWorkflowException>(() => arrangement.Activate(Made.AddDays(41)));

        Assert.Contains("never resumed", failure.Message, StringComparison.Ordinal);
        Assert.Equal(PaymentArrangementStatus.Broken, arrangement.Status);
    }

    [Fact]
    public void A_broken_arrangement_stays_broken_even_after_a_late_payment_settles_it()
    {
        // A STORED TERMINAL STATUS WINS OUTRIGHT. Once the utility has recorded the break, paying up
        // does not un-record it — otherwise "cannot be resumed" would be undone by the next receipt.
        var arrangement = An(balance: 300.00m, instalmentCount: 3);
        arrangement.Activate(Made);
        arrangement.Break(Made.AddDays(40));
        arrangement.Apply(300.00m, Now);

        Assert.Equal(PaymentArrangementStatus.Broken, arrangement.StandingOn(Made.AddDays(41)));
        Assert.False(arrangement.SuppressesDisconnectionOn(Made.AddDays(41)));
    }

    [Fact]
    public void A_kept_arrangement_cannot_be_broken()
    {
        var arrangement = An(balance: 300.00m, instalmentCount: 3);
        arrangement.Activate(Made);
        arrangement.Apply(300.00m, Now);
        arrangement.Keep(Made.AddDays(90));

        Assert.Throws<RegistryWorkflowException>(() => arrangement.Break(Made.AddDays(91)));
    }

    [Fact]
    public void An_arrangement_with_money_still_owing_cannot_be_recorded_as_kept()
    {
        var arrangement = An(balance: 300.00m, instalmentCount: 3);
        arrangement.Activate(Made);
        arrangement.Apply(100.00m, Now);

        var failure = Assert.Throws<RegistryWorkflowException>(() => arrangement.Keep(Made.AddDays(90)));

        Assert.Contains("200.00 outstanding", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_proposal_cannot_be_kept_or_broken_without_coming_into_force_first()
    {
        var arrangement = An();

        Assert.Throws<RegistryWorkflowException>(() => arrangement.Break(Made));
        Assert.Throws<RegistryWorkflowException>(() => arrangement.Keep(Made));
    }

    [Fact]
    public void It_stamps_the_ceilings_that_governed_it()
    {
        // What pays for ArrangementLimit not being effective-dated: re-cutting a rep's authority
        // cannot rewrite whether an arrangement already made needed approving.
        var limit = ArrangementLimits.For(CustomerClass.Residential)!;
        var arrangement = An(balance: 300.00m, limit: limit);

        Assert.Equal(limit.MaximumBalance, arrangement.LimitMaximumBalance);
        Assert.Equal(limit.MaximumInstalments, arrangement.LimitMaximumInstalments);
        Assert.False(arrangement.RequiresApproval);
        Assert.Null(arrangement.ApprovalRequestId);
    }

    [Fact]
    public void An_over_limit_arrangement_cannot_be_recorded_without_an_approval_request()
    {
        // FAILURE PATH. A proposal nobody could ever approve is worse than a refused call: it would
        // sit in the register looking like a promise.
        var limit = ArrangementLimits.For(CustomerClass.Residential)!;

        var failure = Assert.Throws<RegistryValidationException>(() =>
            An(balance: limit.MaximumBalance + 100.00m, instalmentCount: 3, limit: limit));

        Assert.Contains("beyond the published limit", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_over_limit_arrangement_records_the_request_that_has_to_decide_it()
    {
        var limit = ArrangementLimits.For(CustomerClass.Residential)!;
        var approvalId = Guid.CreateVersion7();

        var arrangement = An(
            balance: limit.MaximumBalance + 100.00m,
            limit: limit,
            approvalRequestId: approvalId);

        Assert.True(arrangement.RequiresApproval);
        Assert.Equal(approvalId, arrangement.ApprovalRequestId);
    }

    [Fact]
    public void A_schedule_that_does_not_add_up_to_the_balance_is_refused()
    {
        // Asserted in code rather than trusted from the builder — the same call the double-entry
        // rule makes about debits and credits.
        var failure = Assert.Throws<RegistryValidationException>(() => PaymentArrangement.Propose(
            "PA-000001",
            ArrangementFixtures.Account(),
            ArrangementFixtures.Customer(),
            300.00m,
            "USD",
            0m,
            3,
            ArrangementSchedule.DefaultIntervalDays,
            Made,
            ArrangementLimits.For(CustomerClass.Residential)!,
            approvalRequestId: null,
            [new ScheduledInstalment(1, Made.AddDays(30), 250.00m, false)],
            notes: null,
            RegistryActor.Of(new FakeCurrentUser("auth0|cs-agent")),
            Now));

        Assert.Contains("adds up to 250.00", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_schedule_is_refused() =>
        Assert.Throws<RegistryValidationException>(() => PaymentArrangement.Propose(
            "PA-000001",
            ArrangementFixtures.Account(),
            ArrangementFixtures.Customer(),
            300.00m,
            "USD",
            0m,
            3,
            ArrangementSchedule.DefaultIntervalDays,
            Made,
            ArrangementLimits.For(CustomerClass.Residential)!,
            approvalRequestId: null,
            [],
            notes: null,
            RegistryActor.Of(new FakeCurrentUser("auth0|cs-agent")),
            Now));
}
