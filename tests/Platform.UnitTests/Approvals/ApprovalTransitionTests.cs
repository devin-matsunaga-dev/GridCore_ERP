using GridCore.Platform.Approvals;
using GridCore.Platform.Security;

namespace GridCore.Platform.UnitTests.Approvals;

public class ApprovalTransitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 30, 0, TimeSpan.Zero);

    private static readonly ICurrentUser Requester = new FakeCurrentUser("user-req", "warehouse");

    private static readonly ICurrentUser Approver =
        new FakeCurrentUser("user-app", "manager", Permissions.Purchasing.Approve);

    private static ApprovalRequest APendingRequest() => ApprovalRequest.Raise(
        "purchasing.purchase-order",
        "purchasing.purchase_order",
        "po-42",
        Permissions.Purchasing.Approve,
        payload: new { Total = 12_500m },
        reason: "Replacement transformers",
        Requester,
        Now);

    [Theory]
    [InlineData(ApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.Cancelled)]
    public void A_pending_request_may_reach_any_decided_state(ApprovalStatus outcome) =>
        Assert.True(ApprovalTransitions.IsAllowed(ApprovalStatus.Pending, outcome));

    [Theory]
    [InlineData(ApprovalStatus.Approved, ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.Rejected, ApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.Cancelled, ApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.Approved, ApprovalStatus.Pending)]
    [InlineData(ApprovalStatus.Pending, ApprovalStatus.Pending)]
    public void A_decision_is_final(ApprovalStatus from, ApprovalStatus to) =>
        Assert.False(ApprovalTransitions.IsAllowed(from, to));

    [Fact]
    public void The_allowed_transitions_are_what_a_UI_renders_as_buttons()
    {
        Assert.Equal(
            [ApprovalStatus.Approved, ApprovalStatus.Rejected, ApprovalStatus.Cancelled],
            ApprovalTransitions.AllowedFrom(ApprovalStatus.Pending));

        Assert.Empty(ApprovalTransitions.AllowedFrom(ApprovalStatus.Approved));
    }

    [Fact]
    public void Approving_records_who_decided_and_when()
    {
        var request = APendingRequest();

        request.Approve(Approver, "Budget confirmed", Now.AddHours(2));

        Assert.Equal(ApprovalStatus.Approved, request.Status);
        Assert.Equal("user-app", request.DecidedByUserId);
        Assert.Equal("manager", request.DecidedByUserName);
        Assert.Equal(Now.AddHours(2), request.DecidedAt);
        Assert.Equal("Budget confirmed", request.DecisionNote);
    }

    [Fact]
    public void A_request_cannot_be_decided_twice()
    {
        var request = APendingRequest();
        request.Approve(Approver, null, Now);

        var refused = Assert.Throws<ApprovalWorkflowException>(() => request.Reject(Approver, null, Now));

        Assert.Contains("already Approved", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ApprovalStatus.Approved, request.Status);
    }

    [Fact]
    public void A_request_cannot_be_decided_by_the_person_who_raised_it()
    {
        var request = APendingRequest();
        var requesterWhoCouldApprove =
            new FakeCurrentUser("user-req", "warehouse", Permissions.Purchasing.Approve);

        var refused = Assert.Throws<ApprovalWorkflowException>(
            () => request.Approve(requesterWhoCouldApprove, null, Now));

        Assert.Contains("raised it", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ApprovalStatus.Pending, request.Status);
    }

    [Fact]
    public void Only_the_requester_may_withdraw_a_request()
    {
        var request = APendingRequest();

        Assert.Throws<ApprovalWorkflowException>(() => request.Cancel(Approver, null, Now));

        request.Cancel(Requester, "No longer needed", Now);

        Assert.Equal(ApprovalStatus.Cancelled, request.Status);
    }

    [Fact]
    public void A_request_naming_a_permission_GridCore_does_not_declare_is_refused()
    {
        var refused = Assert.Throws<ApprovalValidationException>(() => ApprovalRequest.Raise(
            "purchasing.purchase-order",
            "purchasing.purchase_order",
            "po-42",
            "purchasing.rubber-stamp",
            payload: null,
            reason: null,
            Requester,
            Now));

        Assert.Contains("not a permission GridCore declares", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "purchasing.purchase_order", "po-42")]
    [InlineData("purchasing.purchase-order", " ", "po-42")]
    [InlineData("purchasing.purchase-order", "purchasing.purchase_order", "")]
    public void A_request_missing_what_it_is_about_is_refused(string requestType, string subjectType, string subjectId) =>
        Assert.Throws<ApprovalValidationException>(() => ApprovalRequest.Raise(
            requestType,
            subjectType,
            subjectId,
            Permissions.Purchasing.Approve,
            payload: null,
            reason: null,
            Requester,
            Now));
}
