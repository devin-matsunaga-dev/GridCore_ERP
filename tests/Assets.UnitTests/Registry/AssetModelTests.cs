using GridCore.Modules.Assets.Data;
using GridCore.Modules.Assets.Features.Assets;
using GridCore.Modules.Assets.UnitTests.Infrastructure;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Assets.UnitTests.Registry;

/// <summary>The assets schema as EF actually builds it.</summary>
public class AssetModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly RegistryActor Engineer = new("subject-1", "Ray Manglona");

    private static Asset Register(string tag, string? serialNumber = null, GeoPosition? position = null) =>
        Asset.Register(tag, AssetClass.Transformer, "Songsong Substation Transformer T-3", Engineer, Now, serialNumber, position: position);

    [Fact]
    public void The_module_owns_a_schema_of_its_own_and_names_its_tables_in_snake_case()
    {
        using var host = new AssetsTestHost();

        using var context = host.NewAssetsContext();

        var model = context.Model;

        Assert.Equal(AssetsDbContext.SchemaName, model.GetDefaultSchema());
        Assert.Equal("assets", model.FindEntityType(typeof(Asset))!.GetTableName());
        Assert.Equal("asset_history", model.FindEntityType(typeof(AssetHistoryEntry))!.GetTableName());
    }

    [Fact]
    public void The_computed_position_is_not_a_column()
    {
        // It is derived from latitude and longitude. Mapped, EF would want a backing field it has
        // no way to find.
        using var host = new AssetsTestHost();

        using var context = host.NewAssetsContext();

        Assert.Null(context.Model.FindEntityType(typeof(Asset))!.FindProperty(nameof(Asset.Position)));
    }

    [Fact]
    public async Task Two_assets_cannot_share_a_tag()
    {
        // Failure path at the database, not in code: the unique index is what makes "one tag, one
        // asset" true even when two registrations race the tag generator.
        using var host = new AssetsTestHost();

        await using var context = host.NewAssetsContext();

        context.Assets.Add(Register("AST-000001", "ABB-1"));
        context.Assets.Add(Register("AST-000001", "ABB-2"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_assets_cannot_share_a_serial_number()
    {
        // One physical machine, one record — enforced by the database, not only by the service's
        // own check.
        using var host = new AssetsTestHost();

        await using var context = host.NewAssetsContext();

        context.Assets.Add(Register("AST-000001", "ABB-T-884213"));
        context.Assets.Add(Register("AST-000002", "ABB-T-884213"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Any_number_of_assets_may_carry_no_serial_number()
    {
        // Both Postgres and SQLite treat NULLs in a unique index as distinct, which is what lets a
        // register hold a thousand poles.
        using var host = new AssetsTestHost();

        await using var context = host.NewAssetsContext();

        context.Assets.Add(Register("AST-000001"));
        context.Assets.Add(Register("AST-000002"));

        await context.SaveChangesAsync();

        Assert.Equal(2, await context.Assets.CountAsync());
    }

    [Fact]
    public async Task A_position_survives_the_round_trip_to_the_sixth_decimal_place()
    {
        // decimal all the way down. A float column would return 14.140832999999999 here, and the
        // pin would land somewhere nobody surveyed.
        using var host = new AssetsTestHost();

        await using (var write = host.NewAssetsContext())
        {
            write.Assets.Add(Register("AST-000001", position: GeoPosition.Create(14.140833m, 145.184722m)));

            await write.SaveChangesAsync();
        }

        await using var read = host.NewAssetsContext();

        var stored = await read.Assets.SingleAsync();

        Assert.Equal(14.140833m, stored.Latitude);
        Assert.Equal(145.184722m, stored.Longitude);
        Assert.Equal(new GeoPosition(14.140833m, 145.184722m), stored.Position);
    }

    [Fact]
    public async Task An_install_date_survives_the_round_trip_as_a_date()
    {
        using var host = new AssetsTestHost();

        await using (var write = host.NewAssetsContext())
        {
            write.Assets.Add(Asset.Register(
                "AST-000001",
                AssetClass.Transformer,
                "Songsong Substation Transformer T-3",
                Engineer,
                Now,
                installedOn: new DateOnly(2009, 3, 2)));

            await write.SaveChangesAsync();
        }

        await using var read = host.NewAssetsContext();

        Assert.Equal(new DateOnly(2009, 3, 2), (await read.Assets.SingleAsync()).InstalledOn);
    }

    private static async Task<AssetsTestHost> WithOneRegisteredAssetAsync()
    {
        var host = new AssetsTestHost();

        await using var write = host.NewAssetsContext();

        write.Assets.Add(Register("AST-000001"));

        await write.SaveChangesAsync();

        return host;
    }

    [Fact]
    public async Task A_status_is_stored_by_name_so_it_survives_a_reordered_enum()
    {
        using var host = await WithOneRegisteredAssetAsync();

        await using var read = host.NewAssetsContext();

        var stored = await read.Database
            .SqlQuery<string>($"""select status as "Value" from assets where asset_tag = 'AST-000001'""")
            .SingleAsync();

        Assert.Equal(nameof(AssetStatus.InStorage), stored);
    }

    [Fact]
    public async Task A_class_is_stored_by_name()
    {
        using var host = await WithOneRegisteredAssetAsync();

        await using var read = host.NewAssetsContext();

        var stored = await read.Database
            .SqlQuery<string>($"""select class as "Value" from assets where asset_tag = 'AST-000001'""")
            .SingleAsync();

        Assert.Equal(nameof(AssetClass.Transformer), stored);
    }

    [Fact]
    public async Task A_condition_is_stored_by_name()
    {
        using var host = await WithOneRegisteredAssetAsync();

        await using var read = host.NewAssetsContext();

        var stored = await read.Database
            .SqlQuery<string>($"""select condition as "Value" from assets where asset_tag = 'AST-000001'""")
            .SingleAsync();

        Assert.Equal(nameof(AssetCondition.Unknown), stored);
    }

    [Fact]
    public async Task A_history_line_appended_to_a_tracked_asset_is_inserted_rather_than_updated()
    {
        // WP-1.2's half-hour: EF decides whether an untracked child of a tracked parent is an insert
        // or an update by asking whether its key is set on a store-generated column. Without
        // ValueGeneratedNever the appended line is tracked as Modified and the save throws
        // DbUpdateConcurrencyException having affected nothing.
        using var host = new AssetsTestHost();

        await using (var write = host.NewAssetsContext())
        {
            write.Assets.Add(Register("AST-000001"));

            await write.SaveChangesAsync();
        }

        await using (var change = host.NewAssetsContext())
        {
            var asset = await change.Assets.Include(candidate => candidate.History).SingleAsync();

            asset.ChangeStatus(AssetStatus.InService, Engineer, Now.AddDays(1), "Energised on bay 3");

            await change.SaveChangesAsync();
        }

        await using var read = host.NewAssetsContext();

        Assert.Equal(2, await read.AssetHistory.CountAsync());
    }

    [Fact]
    public async Task An_assets_history_goes_with_it_and_can_be_read_without_it()
    {
        using var host = new AssetsTestHost();

        await using (var write = host.NewAssetsContext())
        {
            var asset = Register("AST-000001");

            asset.ChangeStatus(AssetStatus.InService, Engineer, Now.AddDays(1), "Energised");

            write.Assets.Add(asset);

            await write.SaveChangesAsync();
        }

        await using var read = host.NewAssetsContext();

        // The set exists for exactly this: the history endpoint reads one asset's lines without
        // loading the asset, and WP-3.4 writes maintenance lines through it.
        Assert.Equal(2, await read.AssetHistory.CountAsync());
    }
}
