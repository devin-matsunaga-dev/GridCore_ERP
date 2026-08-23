using GridCore.Platform.Approvals;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Security;
using GridCore.Platform.UnitTests.Data;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Platform.UnitTests.Approvals;

public class ApprovalServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 30, 0, TimeSpan.Zero);

    private static readonly ApprovalRequestInput AnAdjustment = new(
        "billing.adjustment",
        "billing.bill",
        "b-1001",
        Permissions.Billing.Adjust,
        Payload: new { From = 120.50m, To = 95.00m },
        Reason: "Estimated read corrected");

    private sealed class Harness : IDisposable
    {
        private readonly PlatformTestDatabase _database = new();

        public FakeClock Clock { get; } = new(Now);

        public RecordingNotificationSender Notifications { get; } = new();

        public PlatformDbContext Reader => _database.NewContext();

        public IApprovalService As(ICurrentUser user) => new ApprovalService(
            _database.Context,
            new AuditLog(_database.Context, user, Clock),
            Notifications,
            user,
            Clock);

        public void Dispose() => _database.Dispose();
    }

    private static ICurrentUser Clerk => new FakeCurrentUser("user-clerk", "customer-service");

    private static ICurrentUser Approver =>
        new FakeCurrentUser("user-mgr", "manager", Permissions.Platform.Approve, Permissions.Billing.Adjust);

    [Fact]
    public async Task Raising_a_request_audits_it_and_tells_the_approvers()
    {
        using var harness = new Harness();

        var request = await harness.As(Clerk).RequestAsync(AnAdjustment);

        Assert.Equal(ApprovalStatus.Pending, request.Status);
        Assert.Equal("user-clerk", request.RequestedByUserId);
        Assert.Equal(Now, request.RequestedAt);

        await using var reader = harness.Reader;
        var entry = await reader.AuditEntries.SingleAsync();

        Assert.Equal(AuditActions.ApprovalRequested, entry.Action);
        Assert.Equal(AuditEntityTypes.ApprovalRequest, entry.EntityType);
        Assert.Equal(request.Id.ToString(), entry.EntityId);
        Assert.Equal("user-clerk", entry.UserId);
        Assert.Null(entry.BeforeJson);
        Assert.Contains("Pending", entry.AfterJson!, StringComparison.Ordinal);

        Assert.Equal(Permissions.Billing.Adjust, Assert.Single(harness.Notifications.Sent).Recipient);
    }

    [Fact]
    public async Task Approving_audits_the_before_and_after_and_tells_the_requester()
    {
        using var harness = new Harness();
        var request = await harness.As(Clerk).RequestAsync(AnAdjustment);

        harness.Clock.Advance(TimeSpan.FromHours(3));

        var decided = await harness.As(Approver).ApproveAsync(request.Id, "Read verified");

        Assert.Equal(ApprovalStatus.Approved, decided.Status);
        Assert.Equal("user-mgr", decided.DecidedByUserId);
        Assert.Equal(Now.AddHours(3), decided.DecidedAt);

        await using var reader = harness.Reader;
        var entry = await reader.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.ApprovalApproved);

        Assert.Equal("user-mgr", entry.UserId);
        Assert.Contains("Pending", entry.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains("Approved", entry.AfterJson!, StringComparison.Ordinal);

        Assert.Equal("user-clerk", harness.Notifications.Sent[^1].Recipient);
    }

    [Fact]
    public async Task Deciding_without_the_permission_the_request_demands_is_refused_and_changes_nothing()
    {
        using var harness = new Harness();
        var request = await harness.As(Clerk).RequestAsync(AnAdjustment);

        // Holds platform.approve, so the endpoint would let them in — but not billing.adjust,
        // which is what this particular request demands.
        var wrongApprover = new FakeCurrentUser("user-sup", "supervisor", Permissions.Platform.Approve);

        var refused = await Assert.ThrowsAsync<ApprovalPermissionException>(
            () => harness.As(wrongApprover).ApproveAsync(request.Id));

        Assert.Contains(Permissions.Billing.Adjust, refused.Message, StringComparison.Ordinal);

        await using var reader = harness.Reader;

        Assert.Equal(ApprovalStatus.Pending, (await reader.ApprovalRequests.SingleAsync()).Status);
        Assert.Equal(1, await reader.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task Deciding_a_request_that_does_not_exist_is_a_not_found()
    {
        using var harness = new Harness();

        await Assert.ThrowsAsync<ApprovalNotFoundException>(
            () => harness.As(Approver).ApproveAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task A_rejected_request_cannot_then_be_approved()
    {
        using var harness = new Harness();
        var request = await harness.As(Clerk).RequestAsync(AnAdjustment);
        await harness.As(Approver).RejectAsync(request.Id, "Meter reading stands");

        await Assert.ThrowsAsync<ApprovalWorkflowException>(() => harness.As(Approver).ApproveAsync(request.Id));

        await using var reader = harness.Reader;

        Assert.Equal(ApprovalStatus.Rejected, (await reader.ApprovalRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task Withdrawing_needs_no_permission_but_only_the_requester_may()
    {
        using var harness = new Harness();
        var request = await harness.As(Clerk).RequestAsync(AnAdjustment);

        await Assert.ThrowsAsync<ApprovalWorkflowException>(() => harness.As(Approver).CancelAsync(request.Id));

        harness.Clock.Advance(TimeSpan.FromMinutes(1));

        var withdrawn = await harness.As(Clerk).CancelAsync(request.Id, "Raised in error");

        Assert.Equal(ApprovalStatus.Cancelled, withdrawn.Status);

        await using var reader = harness.Reader;

        Assert.Equal(
            AuditActions.ApprovalCancelled,
            (await reader.AuditEntries.OrderBy(entry => entry.Id).LastAsync()).Action);
    }

    [Fact]
    public async Task The_queue_is_newest_first_and_filterable_by_status()
    {
        using var harness = new Harness();
        var clerk = harness.As(Clerk);

        var first = await clerk.RequestAsync(AnAdjustment);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await clerk.RequestAsync(AnAdjustment with { SubjectId = "b-1002" });

        await harness.As(Approver).ApproveAsync(first.Id);

        var pending = await harness.As(Approver).ListAsync(ApprovalStatus.Pending);
        var all = await harness.As(Approver).ListAsync();

        Assert.Equal(second.Id, Assert.Single(pending).Id);
        Assert.Equal([second.Id, first.Id], all.Select(request => request.Id));
    }

    [Fact]
    public async Task The_page_size_is_capped_however_much_a_caller_asks_for()
    {
        using var harness = new Harness();
        var clerk = harness.As(Clerk);

        foreach (var index in Enumerable.Range(0, 3))
        {
            await clerk.RequestAsync(AnAdjustment with { SubjectId = $"b-{index}" });
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(3, (await harness.As(Approver).ListAsync(limit: int.MaxValue)).Count);
        Assert.Single(await harness.As(Approver).ListAsync(limit: 1));
        Assert.Single(await harness.As(Approver).ListAsync(limit: 0));
    }
}
