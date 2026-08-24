using GridCore.Modules.Assets.Features.Assets;

namespace GridCore.Modules.Assets.UnitTests.Registry;

/// <summary>The asset lifecycle graph on its own, with no entity to hold.</summary>
public class AssetTransitionsTests
{
    [Theory]
    [InlineData(AssetStatus.InStorage, AssetStatus.InService)]
    [InlineData(AssetStatus.InStorage, AssetStatus.Retired)]
    [InlineData(AssetStatus.InService, AssetStatus.UnderMaintenance)]
    [InlineData(AssetStatus.InService, AssetStatus.InStorage)]
    [InlineData(AssetStatus.InService, AssetStatus.Retired)]
    [InlineData(AssetStatus.UnderMaintenance, AssetStatus.InService)]
    [InlineData(AssetStatus.UnderMaintenance, AssetStatus.InStorage)]
    [InlineData(AssetStatus.UnderMaintenance, AssetStatus.Retired)]
    public void The_working_life_of_a_piece_of_plant_is_allowed(AssetStatus from, AssetStatus to) =>
        Assert.True(AssetTransitions.IsAllowed(from, to));

    [Fact]
    public void Stock_cannot_go_straight_to_maintenance() =>
        // Maintenance is work on plant that was doing a job and has been withdrawn. Refurbishing
        // something that never left the yard is stock work, and calling it maintenance would put a
        // job on a service record that no outage and no customer ever saw.
        Assert.False(AssetTransitions.IsAllowed(AssetStatus.InStorage, AssetStatus.UnderMaintenance));

    [Theory]
    [InlineData(AssetStatus.InStorage)]
    [InlineData(AssetStatus.InService)]
    [InlineData(AssetStatus.UnderMaintenance)]
    [InlineData(AssetStatus.Retired)]
    public void Nothing_comes_back_from_retired(AssetStatus to) =>
        Assert.False(AssetTransitions.IsAllowed(AssetStatus.Retired, to));

    [Fact]
    public void Retired_is_the_only_terminal_status() =>
        Assert.Equal(
            [AssetStatus.Retired],
            Enum.GetValues<AssetStatus>().Where(status => AssetTransitions.AllowedFrom(status).Count is 0));

    [Fact]
    public void No_status_can_move_to_itself() =>
        // "Already InService" is a 409 the aggregate raises; a self-loop in the graph would make it
        // a silent no-op that still wrote a history line.
        Assert.All(
            Enum.GetValues<AssetStatus>(),
            status => Assert.DoesNotContain(status, AssetTransitions.AllowedFrom(status)));

    [Fact]
    public void Every_declared_status_is_reachable_from_where_an_asset_starts()
    {
        // A status nothing can reach is a pill, a filter and a report written for a state that
        // never happens. Walked rather than asserted per-status, so adding one to the enum without
        // wiring it into the graph fails here.
        var reached = new HashSet<AssetStatus> { AssetTransitions.Initial };
        var queue = new Queue<AssetStatus>([AssetTransitions.Initial]);

        while (queue.Count > 0)
        {
            foreach (var next in AssetTransitions.AllowedFrom(queue.Dequeue()).Where(reached.Add))
            {
                queue.Enqueue(next);
            }
        }

        Assert.Equal(Enum.GetValues<AssetStatus>().ToHashSet(), reached);
    }

    [Fact]
    public void An_undeclared_status_leads_nowhere_rather_than_throwing() =>
        Assert.Empty(AssetTransitions.AllowedFrom((AssetStatus)99));

    [Theory]
    [InlineData(AssetStatus.InStorage, true)]
    [InlineData(AssetStatus.InService, true)]
    [InlineData(AssetStatus.UnderMaintenance, true)]
    [InlineData(AssetStatus.Retired, false)]
    public void Only_retired_plant_leaves_the_books(AssetStatus status, bool onTheBooks) =>
        // What keeps scrapped plant out of a maintenance plan without deleting the history hanging
        // off it.
        Assert.Equal(onTheBooks, AssetTransitions.IsOnTheBooks(status));
}
