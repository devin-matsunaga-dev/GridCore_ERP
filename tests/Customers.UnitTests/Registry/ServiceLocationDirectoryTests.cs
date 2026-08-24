using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.UnitTests.Infrastructure;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>
/// The premise registry as the rest of GridCore reads it — GridCore's first cross-module read seam
/// (WP-2.1). Metering fits a meter to a premise it may neither reference nor query, so everything
/// it is allowed to know about one comes through here.
/// </summary>
public sealed class ServiceLocationDirectoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    private readonly CustomersTestHost _host = new(new FakeClock(Now));

    public void Dispose() => _host.Dispose();

    private static ServiceLocationInput APremise(
        string line1 = "128 As Nieves Road",
        string city = "Songsong",
        string region = "Rota",
        bool isActive = true) =>
        new(Address.Create(line1, city, region, "MP", postalCode: "96951"), "Meter on the north wall", isActive);

    private Task<ServiceLocation> RegisterAsync(ServiceLocationInput? input = null) =>
        _host.WithLocationsAsync(locations => locations.RegisterAsync(input ?? APremise()));

    [Fact]
    public async Task A_premise_comes_back_summarised_rather_than_as_an_entity()
    {
        var registered = await RegisterAsync();

        var summary = await _host.WithDirectoryAsync(directory => directory.FindAsync(registered.Id));

        Assert.NotNull(summary);
        Assert.Equal(registered.Id, summary.Id);
        Assert.Equal("L-000001", summary.LocationCode);
        Assert.Equal("Songsong", summary.City);
        Assert.Equal("Rota", summary.Region);
        Assert.True(summary.IsActive);

        // The module's own one-line rendering, not a second one assembled by the consumer — so a
        // meter register and a work-order header show the same address.
        Assert.Equal(registered.Address.OneLine, summary.FormattedAddress);
    }

    [Fact]
    public async Task An_id_that_matches_nothing_answers_nothing() =>
        Assert.Null(await _host.WithDirectoryAsync(directory => directory.FindAsync(Guid.CreateVersion7())));

    [Fact]
    public async Task A_deactivated_premise_is_still_readable_and_says_it_is_out_of_service()
    {
        // Not hidden: a meter fitted before the premise went out of service still has to render,
        // and the consumer is the one that decides what "not active" means for its own rules.
        var registered = await RegisterAsync();

        await _host.WithLocationsAsync(locations =>
            locations.UpdateAsync(registered.Id, APremise(isActive: false) with { StatusReason = "Demolished" }));

        var summary = await _host.WithDirectoryAsync(directory => directory.FindAsync(registered.Id));

        Assert.NotNull(summary);
        Assert.False(summary.IsActive);
    }

    [Fact]
    public async Task Several_premises_are_answered_in_one_call_keyed_by_id()
    {
        var first = await RegisterAsync();
        var second = await RegisterAsync(APremise("14 Tatachog Street"));

        var found = await _host.WithDirectoryAsync(directory =>
            directory.FindManyAsync([first.Id, second.Id]));

        Assert.Equal(2, found.Count);
        Assert.Equal("L-000001", found[first.Id].LocationCode);
        Assert.Equal("L-000002", found[second.Id].LocationCode);
    }

    [Fact]
    public async Task A_repeated_id_is_asked_for_once_and_answered_once()
    {
        var premise = await RegisterAsync();

        var found = await _host.WithDirectoryAsync(directory =>
            directory.FindManyAsync([premise.Id, premise.Id, premise.Id]));

        Assert.Single(found);
    }

    [Fact]
    public async Task An_id_that_matches_nothing_is_simply_absent_rather_than_failing_the_batch()
    {
        // A caller rendering a list has to cope with a premise it cannot resolve anyway; throwing
        // would make one bad id lose the whole page.
        var premise = await RegisterAsync();

        var found = await _host.WithDirectoryAsync(directory =>
            directory.FindManyAsync([premise.Id, Guid.CreateVersion7()]));

        Assert.Single(found);
        Assert.True(found.ContainsKey(premise.Id));
    }

    [Fact]
    public async Task Asking_for_nothing_queries_nothing() =>
        Assert.Empty(await _host.WithDirectoryAsync(directory => directory.FindManyAsync([])));

    [Fact]
    public async Task The_serviceable_list_leaves_out_premises_that_are_out_of_service()
    {
        // This is what stops another module's demo seeder quietly fitting a meter at a demolished
        // premise.
        var live = await RegisterAsync();
        var dead = await RegisterAsync(APremise("87 Airport Road"));

        await _host.WithLocationsAsync(locations =>
            locations.UpdateAsync(dead.Id, APremise("87 Airport Road", isActive: false) with { StatusReason = "Demolished" }));

        var serviceable = await _host.WithDirectoryAsync(directory => directory.ListServiceableAsync(50));

        Assert.Equal(live.Id, Assert.Single(serviceable).Id);
    }

    [Fact]
    public async Task The_serviceable_list_never_returns_more_than_the_registries_page_size()
    {
        await RegisterAsync();

        var serviceable = await _host.WithDirectoryAsync(directory => directory.ListServiceableAsync(int.MaxValue));

        Assert.True(serviceable.Count <= ServiceLocationDirectory.MaxPageSize);
    }

    [Fact]
    public void The_summary_carries_no_entity_and_no_EF_type()
    {
        // The boundary, asserted. Handing the entity across would let a consumer walk a navigation
        // into tables it must never read — and CONVENTIONS.md keeps EF types out of Contracts.
        var properties = typeof(ServiceLocationSummary).GetProperties();

        Assert.All(properties, property =>
            Assert.True(
                property.PropertyType.IsPrimitive
                || property.PropertyType == typeof(string)
                || property.PropertyType == typeof(Guid),
                $"{property.Name} is a {property.PropertyType.Name}, which is not a primitive the boundary may carry."));
    }
}
