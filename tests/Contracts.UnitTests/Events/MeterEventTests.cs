using GridCore.Contracts.Events;

namespace GridCore.Contracts.UnitTests.Events;

/// <summary>
/// The metering vocabulary (WP-2.1). Three facts, and the shape of them is the point: what changes
/// which premise is metered is published, and what does not is not.
/// </summary>
public sealed class MeterEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Every_meter_event_carries_a_version_7_identity_stamped_from_when_it_happened()
    {
        IIntegrationEvent[] events =
        [
            MeterRegistered.For(Now, Guid.CreateVersion7(), "MTR-000001", "SEN-1", "SinglePhase", "InStore"),
            MeterInstalled.For(Now, Guid.CreateVersion7(), "MTR-000001", "SinglePhase", Guid.CreateVersion7()),
            MeterRemoved.For(Now, Guid.CreateVersion7(), "MTR-000001", Guid.CreateVersion7(), "Exchanged"),
        ];

        Assert.All(events, published =>
        {
            Assert.Equal(7, published.EventId.Version);
            Assert.Equal(Now, published.OccurredAt);
        });
    }

    [Fact]
    public void An_installation_names_the_premise_and_not_a_service_account()
    {
        // A meter is fitted to a place. An account is who is billed there, and it outlives neither
        // the premise nor the meter — a consumer that needs it derives it through the premise.
        var premise = Guid.CreateVersion7();
        var installed = MeterInstalled.For(Now, Guid.CreateVersion7(), "MTR-000001", "ThreePhase", premise);

        Assert.Equal(premise, installed.ServiceLocationId);
        Assert.DoesNotContain(
            typeof(MeterInstalled).GetProperties(),
            property => property.Name.Contains("Account", StringComparison.Ordinal));
    }

    [Fact]
    public void A_removal_names_the_premise_it_left_unmetered()
    {
        // Separate from the installation rather than one "assignment changed" event: a removal ends
        // a metered period and leaves a gap a billing run must not silently bridge.
        var premise = Guid.CreateVersion7();
        var removed = MeterRemoved.For(Now, Guid.CreateVersion7(), "MTR-000001", premise, null);

        Assert.Equal(premise, removed.ServiceLocationId);
        Assert.Null(removed.Reason);
    }

    [Fact]
    public void Statuses_and_types_travel_by_name_so_Contracts_takes_no_dependency_on_the_modules_enums()
    {
        var registered = MeterRegistered.For(Now, Guid.CreateVersion7(), "MTR-000001", "SEN-1", "Demand", "InStore");

        Assert.Equal("Demand", registered.MeterType);
        Assert.Equal("InStore", registered.Status);
    }

    [Fact]
    public void Meter_events_created_in_order_sort_in_order_by_identity()
    {
        var earlier = MeterInstalled.For(Now, Guid.CreateVersion7(), "MTR-000001", "SinglePhase", Guid.CreateVersion7());
        var later = MeterRemoved.For(Now.AddMinutes(1), Guid.CreateVersion7(), "MTR-000001", Guid.CreateVersion7(), null);

        // Guid v7 sorts chronologically, so an event log ordered by key is ordered by time.
        Assert.True(string.CompareOrdinal(earlier.EventId.ToString(), later.EventId.ToString()) < 0);
    }
}
