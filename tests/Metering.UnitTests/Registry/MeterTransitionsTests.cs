using GridCore.Modules.Metering.Features.Meters;

namespace GridCore.Modules.Metering.UnitTests.Registry;

/// <summary>
/// The meter state machine on its own — pure, no database, no host. The rules here are what stop a
/// meter being marked installed at nowhere, or a premise holding a meter nobody fitted.
/// </summary>
public sealed class MeterTransitionsTests
{
    [Theory]
    [InlineData(MeterStatus.InStore, MeterStatus.Installed)]
    [InlineData(MeterStatus.InStore, MeterStatus.Retired)]
    [InlineData(MeterStatus.Installed, MeterStatus.Faulty)]
    [InlineData(MeterStatus.Installed, MeterStatus.Removed)]
    [InlineData(MeterStatus.Faulty, MeterStatus.Installed)]
    [InlineData(MeterStatus.Faulty, MeterStatus.Removed)]
    [InlineData(MeterStatus.Removed, MeterStatus.InStore)]
    [InlineData(MeterStatus.Removed, MeterStatus.Retired)]
    public void The_lifecycle_allows_these_moves(MeterStatus from, MeterStatus to) =>
        Assert.True(MeterTransitions.IsAllowed(from, to));

    [Theory]
    // A meter goes out again only after it has been checked in as stock — the whole reason Removed
    // and InStore are different statuses.
    [InlineData(MeterStatus.Removed, MeterStatus.Installed)]
    // Straight from a wall to a shelf would skip the removal that frees the premise.
    [InlineData(MeterStatus.Installed, MeterStatus.InStore)]
    [InlineData(MeterStatus.Faulty, MeterStatus.InStore)]
    // Scrapping a meter that is still on somebody's wall.
    [InlineData(MeterStatus.Installed, MeterStatus.Retired)]
    [InlineData(MeterStatus.Faulty, MeterStatus.Retired)]
    // A meter in a store cannot be faulty in service.
    [InlineData(MeterStatus.InStore, MeterStatus.Faulty)]
    [InlineData(MeterStatus.InStore, MeterStatus.Removed)]
    public void The_lifecycle_refuses_these_moves(MeterStatus from, MeterStatus to) =>
        Assert.False(MeterTransitions.IsAllowed(from, to));

    [Fact]
    public void Retired_is_terminal() =>
        Assert.Empty(MeterTransitions.AllowedFrom(MeterStatus.Retired));

    [Fact]
    public void A_new_meter_starts_in_stock() =>
        Assert.Equal(MeterStatus.InStore, MeterTransitions.Initial);

    [Theory]
    [InlineData(MeterStatus.Installed, true)]
    [InlineData(MeterStatus.Faulty, true)]
    [InlineData(MeterStatus.InStore, false)]
    [InlineData(MeterStatus.Removed, false)]
    [InlineData(MeterStatus.Retired, false)]
    public void A_meter_is_fitted_only_while_it_is_on_a_premise(MeterStatus status, bool fitted) =>
        // Faulty counts as fitted: the device is still on the wall and still holds the premise,
        // which is exactly why it cannot be assigned elsewhere until somebody removes it.
        Assert.Equal(fitted, MeterTransitions.IsFitted(status));

    [Theory]
    [InlineData(MeterStatus.InStore, MeterStatus.Installed)]
    [InlineData(MeterStatus.Installed, MeterStatus.Removed)]
    [InlineData(MeterStatus.Faulty, MeterStatus.Removed)]
    public void Fitting_and_unfitting_are_flagged_as_fitting_changes(MeterStatus from, MeterStatus to) =>
        Assert.True(MeterTransitions.ChangesFitting(from, to));

    [Theory]
    [InlineData(MeterStatus.Installed, MeterStatus.Faulty)]
    [InlineData(MeterStatus.Faulty, MeterStatus.Installed)]
    [InlineData(MeterStatus.Removed, MeterStatus.InStore)]
    [InlineData(MeterStatus.Removed, MeterStatus.Retired)]
    [InlineData(MeterStatus.InStore, MeterStatus.Retired)]
    public void The_rest_of_the_lifecycle_leaves_the_meter_where_it_is(MeterStatus from, MeterStatus to) =>
        Assert.False(MeterTransitions.ChangesFitting(from, to));

    [Fact]
    public void Every_declared_status_is_reachable_from_the_initial_one()
    {
        // A status nothing can reach is a status that will never appear on a screen, and a filter
        // for it would be a permanently empty list.
        var reached = new HashSet<MeterStatus> { MeterTransitions.Initial };
        var frontier = new Queue<MeterStatus>([MeterTransitions.Initial]);

        while (frontier.TryDequeue(out var status))
        {
            foreach (var next in MeterTransitions.AllowedFrom(status).Where(reached.Add))
            {
                frontier.Enqueue(next);
            }
        }

        Assert.Equal(Enum.GetValues<MeterStatus>().ToHashSet(), reached);
    }

    [Fact]
    public void Every_status_that_holds_a_premise_can_give_it_up_again()
    {
        // Otherwise a meter could reach a state where the premise it holds is stranded — no way to
        // free it short of editing the database.
        var fitted = Enum.GetValues<MeterStatus>().Where(MeterTransitions.IsFitted);

        Assert.All(fitted, status =>
            Assert.Contains(MeterTransitions.AllowedFrom(status), next => !MeterTransitions.IsFitted(next)));
    }
}
