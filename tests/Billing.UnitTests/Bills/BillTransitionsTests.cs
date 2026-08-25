using GridCore.Modules.Billing.Features.Bills;

namespace GridCore.Modules.Billing.UnitTests.Bills;

/// <summary>
/// The bill state machine, held out of the aggregate so a UI can ask what is legal without holding
/// one — the shape <c>MeterTransitions</c> and <c>ServiceAccountTransitions</c> established.
/// </summary>
public class BillTransitionsTests
{
    [Theory]
    [InlineData(BillStatus.Draft, BillStatus.Issued)]
    [InlineData(BillStatus.Draft, BillStatus.Cancelled)]
    [InlineData(BillStatus.Issued, BillStatus.PartiallyPaid)]
    [InlineData(BillStatus.Issued, BillStatus.Paid)]
    [InlineData(BillStatus.Issued, BillStatus.Overdue)]
    [InlineData(BillStatus.Issued, BillStatus.Cancelled)]
    [InlineData(BillStatus.PartiallyPaid, BillStatus.Paid)]
    [InlineData(BillStatus.PartiallyPaid, BillStatus.Overdue)]
    [InlineData(BillStatus.Overdue, BillStatus.PartiallyPaid)]
    [InlineData(BillStatus.Overdue, BillStatus.Paid)]
    public void The_legal_moves_are_allowed(BillStatus from, BillStatus to) =>
        Assert.True(BillTransitions.IsAllowed(from, to));

    [Theory]

    // A draft is not owed by anybody, so nobody can pay it. Being asked for the money is what
    // issuing is.
    [InlineData(BillStatus.Draft, BillStatus.Paid)]
    [InlineData(BillStatus.Draft, BillStatus.PartiallyPaid)]
    [InlineData(BillStatus.Draft, BillStatus.Overdue)]

    // A bill does not stop having been late.
    [InlineData(BillStatus.Overdue, BillStatus.Issued)]

    // Nothing goes back to being a draft: it has been sent.
    [InlineData(BillStatus.Issued, BillStatus.Draft)]

    // Both terminal states are terminal. Money coming back after a bill is settled is a refund, and
    // anything owed after a cancellation is a new bill.
    [InlineData(BillStatus.Paid, BillStatus.Issued)]
    [InlineData(BillStatus.Paid, BillStatus.Overdue)]
    [InlineData(BillStatus.Paid, BillStatus.Cancelled)]
    [InlineData(BillStatus.Cancelled, BillStatus.Issued)]
    [InlineData(BillStatus.Cancelled, BillStatus.Draft)]
    public void The_illegal_moves_are_refused(BillStatus from, BillStatus to) =>
        Assert.False(BillTransitions.IsAllowed(from, to));

    [Fact]
    public void No_status_may_move_to_itself() =>
        // A self-transition in a state machine is a way for a bill to "move" to where it already is,
        // which is how a second partial payment would look like a status change. Bill.RecordPayment
        // handles that case explicitly rather than through the machine.
        Assert.All(
            Enum.GetValues<BillStatus>(),
            status => Assert.False(BillTransitions.IsAllowed(status, status)));

    [Theory]
    [InlineData(BillStatus.Issued, true)]
    [InlineData(BillStatus.PartiallyPaid, true)]
    [InlineData(BillStatus.Overdue, true)]
    [InlineData(BillStatus.Draft, false)]
    [InlineData(BillStatus.Paid, false)]
    [InlineData(BillStatus.Cancelled, false)]
    public void Only_a_sent_and_unsettled_bill_is_money_the_utility_is_owed(BillStatus status, bool expected) =>
        // A draft is deliberately not outstanding: it has not been sent, so nobody owes it. That is
        // the difference between a receivable and a calculation, and it is why Finance posts on
        // BillIssued rather than when the run produces the figures.
        Assert.Equal(expected, BillTransitions.IsOutstanding(status));

    [Theory]
    [InlineData(BillStatus.Paid, true)]
    [InlineData(BillStatus.Cancelled, true)]
    [InlineData(BillStatus.Draft, false)]
    [InlineData(BillStatus.Issued, false)]
    [InlineData(BillStatus.PartiallyPaid, false)]
    [InlineData(BillStatus.Overdue, false)]
    public void Paid_and_cancelled_are_the_terminal_states(BillStatus status, bool expected) =>
        Assert.Equal(expected, BillTransitions.IsFinal(status));

    [Fact]
    public void Every_status_is_reachable_from_a_draft()
    {
        // A status nothing can reach is a status that does not exist. Walked rather than asserted
        // per case, so adding one to the enum without a way into it fails here.
        var reached = new HashSet<BillStatus> { BillStatus.Draft };
        var frontier = new Queue<BillStatus>([BillStatus.Draft]);

        while (frontier.Count > 0)
        {
            foreach (var next in BillTransitions.AllowedFrom(frontier.Dequeue()).Where(reached.Add))
            {
                frontier.Enqueue(next);
            }
        }

        Assert.Equal(Enum.GetValues<BillStatus>().ToHashSet(), reached);
    }

    [Fact]
    public void Every_status_the_machine_names_is_one_the_enum_declares() =>
        Assert.All(
            Enum.GetValues<BillStatus>().SelectMany(BillTransitions.AllowedFrom),
            status => Assert.True(Enum.IsDefined(status)));
}
