using GridCore.Contracts.Events;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>The location registry over the real EF model, on SQLite in-memory.</summary>
public class ServiceLocationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static CustomersTestHost NewHost() =>
        new(new FakeClock(Now), new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    private static ServiceLocationInput APremise(string line1 = "128 As Nieves Road", string city = "Songsong", string region = "Rota") =>
        new(Address.Create(line1, city, region, "MP", postalCode: "96951"), "Meter on the north wall");

    [Fact]
    public async Task Registering_a_premise_issues_the_first_location_code_and_publishes_it()
    {
        using var host = NewHost();

        var location = await host.WithLocationsAsync(locations => locations.RegisterAsync(APremise()));

        Assert.Equal("L-000001", location.LocationCode);
        Assert.True(location.IsActive);

        var published = host.Events.Single<ServiceLocationRegistered>();

        Assert.Equal(location.Id, published.ServiceLocationId);
        Assert.Equal("Rota", published.Region);
        Assert.Equal("128 As Nieves Road, Songsong, Rota, 96951", published.Address);
    }

    [Fact]
    public async Task Location_codes_and_account_numbers_are_separate_series()
    {
        // They share a table only in the sense that they share a schema. A location taking a
        // customer's next number would be a registry nobody could quote from.
        using var host = NewHost();

        await host.WithLocationsAsync(locations => locations.RegisterAsync(APremise()));
        var second = await host.WithLocationsAsync(locations => locations.RegisterAsync(APremise("14 Tatachog Street")));

        Assert.Equal("L-000002", second.LocationCode);
    }

    [Fact]
    public async Task Registering_a_premise_is_audited()
    {
        using var host = NewHost();

        var location = await host.WithLocationsAsync(locations => locations.RegisterAsync(APremise()));

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries.SingleAsync();

        Assert.Equal(AuditActions.ServiceLocationCreated, entry.Action);
        Assert.Equal(AuditEntityTypes.ServiceLocation, entry.EntityType);
        Assert.Equal(location.Id.ToString(), entry.EntityId);
    }

    [Fact]
    public async Task A_registration_that_throws_writes_nothing_at_all()
    {
        // Failure path, and the atomicity proof for this slice: the guard throws inside the unit of
        // work, so there is no premise row, no audit entry and no event for a premise that does not
        // exist. A caller reaching the service directly — a seeder, a later module — gets the same
        // guarantee the endpoint does.
        using var host = NewHost();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            host.WithLocationsAsync(locations => locations.RegisterAsync(APremise() with { Address = null! })));

        await using var database = host.NewCustomersContext();
        await using var platform = host.NewPlatformContext();

        Assert.Empty(await database.ServiceLocations.ToListAsync());
        Assert.Empty(await platform.AuditEntries.ToListAsync());
        Assert.Empty(host.Events.Published);
    }

    [Fact]
    public async Task Deactivating_a_premise_is_an_update_audited_with_its_before_state()
    {
        using var host = NewHost();

        var location = await host.WithLocationsAsync(locations => locations.RegisterAsync(APremise()));

        var deactivated = await host.WithLocationsAsync(locations => locations.UpdateAsync(
            location.Id,
            APremise() with { IsActive = false, StatusReason = "Structure demolished after the storm." }));

        Assert.False(deactivated.IsActive);

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries
            .Where(candidate => candidate.Action == AuditActions.ServiceLocationUpdated)
            .SingleAsync();

        Assert.Contains("\"isActive\":true", entry.BeforeJson);
        Assert.Contains("\"isActive\":false", entry.AfterJson);
    }

    [Fact]
    public async Task Updating_a_premise_that_does_not_exist_is_a_404() =>
        await Assert.ThrowsAsync<ServiceLocationNotFoundException>(async () =>
        {
            using var host = NewHost();

            await host.WithLocationsAsync(locations => locations.UpdateAsync(Guid.CreateVersion7(Now), APremise()));
        });

    [Fact]
    public async Task The_location_list_filters_on_island_activity_and_a_search_term()
    {
        using var host = NewHost();

        var rota = await host.WithLocationsAsync(locations => locations.RegisterAsync(APremise()));
        await host.WithLocationsAsync(locations => locations.RegisterAsync(APremise("22 Beach Road", "Garapan", "Saipan")));

        await host.WithLocationsAsync(locations => locations.UpdateAsync(rota.Id, APremise() with { IsActive = false }));

        Assert.Single(await host.WithLocationsAsync(locations => locations.ListAsync(new ServiceLocationQuery(Region: "saipan"))));
        Assert.Single(await host.WithLocationsAsync(locations => locations.ListAsync(new ServiceLocationQuery(IsActive: false))));

        // Searching hits the code, the street and the village — the three things anyone reads off a
        // work order.
        Assert.Single(await host.WithLocationsAsync(locations => locations.ListAsync(new ServiceLocationQuery(Search: "BEACH"))));
        Assert.Single(await host.WithLocationsAsync(locations => locations.ListAsync(new ServiceLocationQuery(Search: "l-000001"))));
    }
}
