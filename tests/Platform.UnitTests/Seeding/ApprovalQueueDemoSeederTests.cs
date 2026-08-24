using GridCore.Platform.Approvals;
using GridCore.Platform.Security;
using GridCore.Platform.Seeding;
using GridCore.Platform.UnitTests.Data;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Platform.UnitTests.Seeding;

/// <summary>
/// The one seeder that ships today. What matters is not the rows themselves but that they are
/// decidable — a demo queue nobody may act on demonstrates nothing.
/// </summary>
public class ApprovalQueueDemoSeederTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 30, 0, TimeSpan.Zero);

    private static async Task<IReadOnlyList<ApprovalRequest>> SeedAsync(PlatformTestDatabase database)
    {
        await new ApprovalQueueDemoSeeder(database.Context, new FakeClock(Now)).SeedAsync(CancellationToken.None);
        await database.Context.SaveChangesAsync();

        await using var reader = database.NewContext();

        return await reader.ApprovalRequests.OrderBy(request => request.Id).ToListAsync();
    }

    [Fact]
    public async Task It_seeds_a_pending_queue()
    {
        using var database = new PlatformTestDatabase();

        var requests = await SeedAsync(database);

        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal(ApprovalStatus.Pending, request.Status));
    }

    [Fact]
    public async Task Every_seeded_request_needs_a_permission_GridCore_declares()
    {
        // The aggregate refuses an unknown permission outright, so this is really an assertion that
        // the seeded queue is decidable at all rather than a row nobody could ever action.
        using var database = new PlatformTestDatabase();

        var requests = await SeedAsync(database);

        Assert.All(requests, request => Assert.Contains(request.RequiredPermission, Permissions.All));
    }

    [Fact]
    public async Task Seeded_requests_are_attributed_to_demo_stand_ins_not_to_the_system()
    {
        // Separation of duties means the requester may not decide their own request. Attributing
        // these to the system would be harmless; attributing them to a real subject id would not.
        using var database = new PlatformTestDatabase();

        var requests = await SeedAsync(database);

        Assert.All(requests, request =>
            Assert.StartsWith(DemoActor.IdPrefix, request.RequestedByUserId, StringComparison.Ordinal));

        Assert.All(requests, request =>
            Assert.NotEqual(SystemUser.SystemUserId, request.RequestedByUserId));
    }

    [Fact]
    public async Task A_signed_in_approver_can_decide_a_seeded_request()
    {
        // The point of the seeder. If the demo world's requests were raised by whoever is signed in,
        // the aggregate would refuse every decision and the queue would look broken.
        using var database = new PlatformTestDatabase();

        var requests = await SeedAsync(database);
        var manager = new FakeCurrentUser("keycloak-subject-mia", "Mia Ops", Permissions.Purchasing.Approve);

        requests[0].Approve(manager, "Looks right.", Now.AddHours(1));

        Assert.Equal(ApprovalStatus.Approved, requests[0].Status);
    }

    [Fact]
    public void A_demo_actor_authorises_nothing()
    {
        // Failure path: the stand-in is a label on a row, never a way to get past a permission gate.
        var actor = new DemoActor("warehouse", "Wes Store (demo)");

        Assert.False(actor.HasPermission(Permissions.Purchasing.Approve));
        Assert.False(actor.HasPermission(Permissions.Platform.Admin));
    }
}
