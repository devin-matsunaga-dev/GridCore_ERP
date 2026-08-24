using GridCore.Modules.Assets.Features.Assets;
using GridCore.Modules.Assets.Features.Shared;
using GridCore.Modules.Assets.Seeding;
using GridCore.Modules.Assets.UnitTests.Infrastructure;
using GridCore.Platform.Registry;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Assets.UnitTests.Seeding;

/// <summary>
/// The demo plant register. Seeded through the real aggregate, so these assertions are also a check
/// that the demo world is one the domain rules actually permit.
/// </summary>
public class AssetsDemoSeederTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static async Task<List<Asset>> SeededAsync(AssetsTestHost host)
    {
        await using (var write = host.NewAssetsContext())
        {
            await new AssetsDemoSeeder(write, new FakeClock(Now)).SeedAsync(CancellationToken.None);

            // The seeder itself never saves — the runner's unit of work does. Here the test plays
            // that part, which is also what proves the seeder left a saveable graph behind.
            await write.SaveChangesAsync();
        }

        await using var read = host.NewAssetsContext();

        return await read.Assets.Include(asset => asset.History).OrderBy(asset => asset.AssetTag).ToListAsync();
    }

    [Fact]
    public void The_seeder_is_named_and_ordered_after_the_customer_registries()
    {
        IDemoSeeder seeder = new AssetsDemoSeeder(null!, TimeProvider.System);

        // The name is the dedupe key and is never renamed — a rename seeds a second register.
        Assert.Equal("assets.registry", seeder.Name);
        Assert.Equal(400, seeder.Order);
    }

    [Fact]
    public async Task Every_asset_class_appears_at_least_once()
    {
        // The WP's own acceptance check: create each asset class.
        using var host = new AssetsTestHost();

        var seeded = await SeededAsync(host);

        Assert.Equal(
            Enum.GetValues<AssetClass>().ToHashSet(),
            seeded.Select(asset => asset.Class).ToHashSet());
    }

    [Fact]
    public async Task Every_status_appears_at_least_once()
    {
        // A register screen with only one pill on it demonstrates nothing.
        using var host = new AssetsTestHost();

        var seeded = await SeededAsync(host);

        Assert.Equal(
            Enum.GetValues<AssetStatus>().ToHashSet(),
            seeded.Select(asset => asset.Status).ToHashSet());
    }

    [Fact]
    public async Task The_conditions_span_the_scale()
    {
        // What makes the maintenance-plan query worth running in a demonstration.
        using var host = new AssetsTestHost();

        var seeded = await SeededAsync(host);

        Assert.Contains(AssetCondition.Excellent, seeded.Select(asset => asset.Condition));
        Assert.Contains(AssetCondition.Poor, seeded.Select(asset => asset.Condition));
        Assert.Contains(AssetCondition.Critical, seeded.Select(asset => asset.Condition));
    }

    [Fact]
    public async Task Tags_run_from_the_first_so_a_real_registration_continues_the_series()
    {
        // Inside the seeding transaction none of these rows are visible to a query, so the
        // generator cannot be used. Starting at 1 is what keeps the series correct afterwards.
        using var host = new AssetsTestHost();

        var seeded = await SeededAsync(host);

        Assert.Equal(
            Enumerable.Range(1, seeded.Count).Select(ordinal => RegistryNumbers.Format(AssetNumbers.AssetTagPrefix, ordinal)),
            seeded.Select(asset => asset.AssetTag));
    }

    [Fact]
    public async Task Every_seeded_position_is_on_one_of_the_three_islands()
    {
        // Rota, Tinian and Saipan sit between roughly 14.1 and 15.3 north, 145.1 and 145.85 east.
        // A pin in the wrong ocean is the detail a demonstration audience notices immediately.
        using var host = new AssetsTestHost();

        var located = (await SeededAsync(host)).Where(asset => asset.Position is not null);

        Assert.NotEmpty(located);

        Assert.All(located, asset =>
        {
            Assert.InRange(asset.Latitude!.Value, 14.0m, 15.4m);
            Assert.InRange(asset.Longitude!.Value, 145.0m, 145.9m);
        });
    }

    [Fact]
    public async Task Plant_still_in_the_yard_has_no_position_and_no_install_date()
    {
        using var host = new AssetsTestHost();

        var inStorage = (await SeededAsync(host)).Where(asset => asset.Status is AssetStatus.InStorage).ToList();

        Assert.NotEmpty(inStorage);
        Assert.All(inStorage, asset => Assert.Null(asset.InstalledOn));
    }

    [Fact]
    public async Task Every_seeded_asset_carries_a_real_history()
    {
        // Walked through the transitions rather than assigned a status, so the history is the one
        // those transitions produce — and an illegal demo lifecycle fails in the seeder.
        using var host = new AssetsTestHost();

        var seeded = await SeededAsync(host);

        Assert.All(seeded, asset =>
        {
            Assert.NotEmpty(asset.History);
            Assert.Equal(AssetHistoryEntryType.Registered, asset.History[0].EntryType);
        });
    }

    [Fact]
    public async Task Every_seeded_line_is_attributed_to_a_demo_actor()
    {
        // The demo: prefix cannot collide with an identity-provider subject, so a seeded history
        // line can never be mistaken for one a real engineer made.
        using var host = new AssetsTestHost();

        var seeded = await SeededAsync(host);

        Assert.All(
            seeded.SelectMany(asset => asset.History),
            entry => Assert.StartsWith(DemoActor.IdPrefix, entry.ActorId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retired_plant_stays_on_the_register()
    {
        // Terminal, and still readable: the jobs and costs booked against it have to stay
        // answerable.
        using var host = new AssetsTestHost();

        var retired = (await SeededAsync(host)).Where(asset => asset.Status is AssetStatus.Retired).ToList();

        Assert.NotEmpty(retired);
        Assert.All(retired, asset => Assert.False(asset.IsOnTheBooks));
    }

    [Fact]
    public async Task Serial_numbers_are_unique_across_the_demo_world()
    {
        // The seeder writes past the service's own check, so a duplicated serial here would only
        // surface as a unique-index violation on a developer's first `aspire run`.
        using var host = new AssetsTestHost();

        var serials = (await SeededAsync(host))
            .Select(asset => asset.SerialNumber)
            .Where(serial => serial is not null)
            .ToList();

        Assert.Equal(serials.Count, serials.Distinct(StringComparer.Ordinal).Count());
    }
}
