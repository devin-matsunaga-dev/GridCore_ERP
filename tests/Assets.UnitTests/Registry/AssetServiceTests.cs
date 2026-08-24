using GridCore.Contracts.Events;
using GridCore.Modules.Assets.Features.Assets;
using GridCore.Modules.Assets.Features.Shared;
using GridCore.Modules.Assets.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Assets.UnitTests.Registry;

/// <summary>
/// The asset register end to end on SQLite in-memory: the write, its history line, its audit entry
/// and its event, all in one transaction.
/// </summary>
public class AssetServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static AssetsTestHost Host(out FakeClock clock)
    {
        clock = new FakeClock(Now);

        return new AssetsTestHost(clock, new FakeCurrentUser("subject-1", "Ray Manglona"));
    }

    private static RegisterAssetInput Transformer(string? serialNumber = null) =>
        new(
            AssetClass.Transformer,
            "Songsong Substation Transformer T-3",
            serialNumber,
            "ABB",
            "ONAN 1500 kVA",
            new DateOnly(2009, 3, 2),
            14.140900m,
            145.184800m,
            "Bay 3, east side of the switchyard");

    [Fact]
    public async Task Registering_an_asset_issues_the_next_tag()
    {
        using var host = Host(out var clock);

        var first = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer("ABB-1")));

        clock.Advance(TimeSpan.FromSeconds(1));

        var second = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer("ABB-2")));

        Assert.Equal("AST-000001", first.AssetTag);
        Assert.Equal("AST-000002", second.AssetTag);
    }

    [Fact]
    public async Task A_registration_stores_everything_the_caller_gave_it()
    {
        using var host = Host(out _);

        var registered = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer("ABB-T-884213")));

        await using var read = host.NewAssetsContext();

        var stored = await read.Assets.SingleAsync(asset => asset.Id == registered.Id);

        Assert.Equal(AssetClass.Transformer, stored.Class);
        Assert.Equal("ABB-T-884213", stored.SerialNumber);
        Assert.Equal("ABB", stored.Manufacturer);
        Assert.Equal(new DateOnly(2009, 3, 2), stored.InstalledOn);
        Assert.Equal(new GeoPosition(14.140900m, 145.184800m), stored.Position);
        Assert.Equal("Bay 3, east side of the switchyard", stored.LocationNote);
    }

    [Fact]
    public async Task A_registration_writes_its_audit_entry_in_the_same_transaction()
    {
        // Invariant 1. The asset row and the audit entry are in two different schemas on one
        // connection, and this is what proves they commit together.
        using var host = Host(out _);

        var asset = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer()));

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.AssetRegistered);

        Assert.Equal(AuditEntityTypes.Asset, entry.EntityType);
        Assert.Equal(asset.Id.ToString(), entry.EntityId);
        Assert.Equal("subject-1", entry.UserId);
        Assert.Null(entry.BeforeJson);
        Assert.NotNull(entry.AfterJson);
    }

    [Fact]
    public async Task A_registration_publishes_that_the_asset_exists()
    {
        using var host = Host(out _);

        var asset = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer()));

        var published = host.Events.Single<AssetRegistered>();

        Assert.Equal(asset.Id, published.AssetId);
        Assert.Equal("AST-000001", published.AssetTag);
        Assert.Equal(nameof(AssetClass.Transformer), published.Class);
        Assert.Equal(nameof(AssetStatus.InStorage), published.Status);
    }

    [Fact]
    public async Task Registering_a_serial_number_twice_is_a_conflict_naming_the_asset_it_collides_with()
    {
        // Failure path: one physical transformer, one record. A second registration of the same
        // serial is the mistake this catches, and the message says which asset already holds it.
        using var host = Host(out var clock);

        await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer("ABB-T-884213")));

        clock.Advance(TimeSpan.FromSeconds(1));

        var failure = await Assert.ThrowsAsync<AssetWorkflowException>(() =>
            host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer("ABB-T-884213"))));

        Assert.Contains("AST-000001", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plant_with_no_serial_number_can_be_registered_over_and_over()
    {
        // A pole and a span of conductor carry no serial, and the unique index treats NULLs as
        // distinct — so any number of them coexist.
        using var host = Host(out var clock);

        foreach (var _ in Enumerable.Range(0, 3))
        {
            await host.WithAssetsAsync(assets => assets.RegisterAsync(
                new RegisterAssetInput(AssetClass.Pole, "Pole R-0472, As Nieves Road")));

            clock.Advance(TimeSpan.FromSeconds(1));
        }

        await using var read = host.NewAssetsContext();

        Assert.Equal(3, await read.Assets.CountAsync());
    }

    [Fact]
    public async Task Half_a_position_is_refused_before_anything_is_written()
    {
        using var host = Host(out _);

        await Assert.ThrowsAsync<AssetValidationException>(() =>
            host.WithAssetsAsync(assets => assets.RegisterAsync(
                new RegisterAssetInput(AssetClass.Pole, "Pole R-0472", Latitude: 14.14m))));

        await using var read = host.NewAssetsContext();

        Assert.Empty(await read.Assets.ToListAsync());
    }

    [Fact]
    public async Task Correcting_an_asset_audits_what_changed()
    {
        using var host = Host(out var clock);

        var asset = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer()));

        clock.Advance(TimeSpan.FromSeconds(1));

        await host.WithAssetsAsync(assets => assets.UpdateAsync(
            asset.Id,
            new UpdateAssetInput(AssetClass.Transformer, "Songsong Substation Transformer T-3", Model: "ONAN 2000 kVA")));

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.AssetUpdated);

        Assert.Contains("ONAN 1500 kVA", entry.BeforeJson, StringComparison.Ordinal);
        Assert.Contains("ONAN 2000 kVA", entry.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Correcting_an_asset_onto_another_assets_serial_number_is_a_conflict()
    {
        using var host = Host(out var clock);

        await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer("ABB-T-884213")));

        clock.Advance(TimeSpan.FromSeconds(1));

        var second = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer("ABB-T-884502")));

        clock.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<AssetWorkflowException>(() =>
            host.WithAssetsAsync(assets => assets.UpdateAsync(
                second.Id,
                new UpdateAssetInput(AssetClass.Transformer, second.Name, SerialNumber: "ABB-T-884213"))));
    }

    [Fact]
    public async Task An_asset_may_keep_its_own_serial_number_through_a_correction()
    {
        // The obvious bug in the check above: excluding the asset being corrected is what stops it
        // colliding with itself on every edit.
        using var host = Host(out var clock);

        var asset = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer("ABB-T-884213")));

        clock.Advance(TimeSpan.FromSeconds(1));

        var corrected = await host.WithAssetsAsync(assets => assets.UpdateAsync(
            asset.Id,
            new UpdateAssetInput(AssetClass.Transformer, "Songsong Substation Transformer T-3A", SerialNumber: "ABB-T-884213")));

        Assert.Equal("Songsong Substation Transformer T-3A", corrected.Name);
    }

    [Fact]
    public async Task Installing_an_asset_audits_the_move_and_publishes_it()
    {
        using var host = Host(out var clock);

        var asset = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer()));

        clock.Advance(TimeSpan.FromSeconds(1));

        await host.WithAssetsAsync(assets => assets.ChangeStatusAsync(asset.Id, AssetStatus.InService, "Energised on bay 3"));

        var published = host.Events.Single<AssetStatusChanged>();

        Assert.Equal(nameof(AssetStatus.InStorage), published.FromStatus);
        Assert.Equal(nameof(AssetStatus.InService), published.ToStatus);
        Assert.Equal("Energised on bay 3", published.Reason);

        await using var platform = host.NewPlatformContext();

        Assert.Single(await platform.AuditEntries.Where(entry => entry.Action == AuditActions.AssetStatusChanged).ToListAsync());
    }

    [Fact]
    public async Task An_illegal_move_publishes_nothing_and_leaves_the_asset_alone()
    {
        // Failure path through the whole stack: the aggregate refuses, the transaction rolls back,
        // and no event escapes to a consumer that would act on a move that did not happen.
        using var host = Host(out var clock);

        var asset = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer()));

        clock.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<AssetWorkflowException>(() =>
            host.WithAssetsAsync(assets => assets.ChangeStatusAsync(asset.Id, AssetStatus.UnderMaintenance, "Not legal from stock")));

        Assert.Empty(host.Events.Published.OfType<AssetStatusChanged>());

        await using var read = host.NewAssetsContext();

        Assert.Equal(AssetStatus.InStorage, (await read.Assets.SingleAsync()).Status);
    }

    [Fact]
    public async Task Grading_an_asset_is_audited_but_published_to_nobody()
    {
        // A condition is this module's own assessment, revised at every inspection. A status is the
        // fact other modules gate on.
        using var host = Host(out var clock);

        var asset = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer()));

        clock.Advance(TimeSpan.FromSeconds(1));

        var graded = await host.WithAssetsAsync(assets =>
            assets.AssessConditionAsync(asset.Id, AssetCondition.Poor, "Spalling at the base"));

        Assert.Equal(AssetCondition.Poor, graded.Condition);
        Assert.Empty(host.Events.Published.OfType<AssetStatusChanged>());

        await using var platform = host.NewPlatformContext();

        Assert.Single(await platform.AuditEntries.Where(entry => entry.Action == AuditActions.AssetConditionAssessed).ToListAsync());
    }

    [Fact]
    public async Task A_missing_asset_is_a_404_rather_than_a_null_reference()
    {
        using var host = Host(out _);

        await Assert.ThrowsAsync<AssetNotFoundException>(() =>
            host.WithAssetsAsync(assets => assets.ChangeStatusAsync(Guid.CreateVersion7(Now), AssetStatus.InService, null)));
    }

    [Fact]
    public async Task The_history_of_an_asset_that_does_not_exist_is_a_404_rather_than_an_empty_list()
    {
        // An empty list would say the asset existed and nothing had happened to it, which is
        // unreachable: every asset is registered with a line.
        using var host = Host(out _);

        await Assert.ThrowsAsync<AssetNotFoundException>(() =>
            host.WithAssetsAsync(assets => assets.HistoryAsync(Guid.CreateVersion7(Now))));
    }

    [Fact]
    public async Task An_assets_history_reads_back_oldest_first()
    {
        using var host = Host(out var clock);

        var asset = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer()));

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.WithAssetsAsync(assets => assets.ChangeStatusAsync(asset.Id, AssetStatus.InService, "Energised"));

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.WithAssetsAsync(assets => assets.AssessConditionAsync(asset.Id, AssetCondition.Good, "Oil sample clear"));

        var history = await host.WithAssetsAsync(assets => assets.HistoryAsync(asset.Id));

        Assert.Equal(
            [AssetHistoryEntryType.Registered, AssetHistoryEntryType.StatusChanged, AssetHistoryEntryType.ConditionAssessed],
            history.Select(entry => entry.EntryType));
    }

    [Fact]
    public async Task The_history_narrows_to_one_kind_of_line()
    {
        // What WP-3.4 reads: the maintenance lines on their own, off a table that grows with every
        // inspection.
        using var host = Host(out var clock);

        var asset = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer()));

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.WithAssetsAsync(assets => assets.ChangeStatusAsync(asset.Id, AssetStatus.InService, "Energised"));

        var lifecycle = await host.WithAssetsAsync(assets =>
            assets.HistoryAsync(asset.Id, AssetHistoryEntryType.StatusChanged));

        Assert.Single(lifecycle);
        Assert.Empty(await host.WithAssetsAsync(assets => assets.HistoryAsync(asset.Id, AssetHistoryEntryType.Maintenance)));
    }

    [Fact]
    public async Task The_register_is_searched_by_whatever_is_legible_on_the_plate()
    {
        using var host = Host(out var clock);

        await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer("ABB-T-884213")));

        clock.Advance(TimeSpan.FromSeconds(1));

        await host.WithAssetsAsync(assets => assets.RegisterAsync(
            new RegisterAssetInput(AssetClass.Pole, "Pole R-0472, As Nieves Road")));

        Assert.Single(await host.WithAssetsAsync(assets => assets.ListAsync(new AssetQuery(Search: "songsong"))));
        Assert.Single(await host.WithAssetsAsync(assets => assets.ListAsync(new AssetQuery(Search: "884213"))));
        Assert.Single(await host.WithAssetsAsync(assets => assets.ListAsync(new AssetQuery(Search: "ast-000002"))));
    }

    [Fact]
    public async Task The_maintenance_plan_query_is_status_and_condition_together()
    {
        using var host = Host(out var clock);

        var transformer = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer("ABB-1")));

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.WithAssetsAsync(assets => assets.ChangeStatusAsync(transformer.Id, AssetStatus.InService, "Energised"));

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.WithAssetsAsync(assets => assets.AssessConditionAsync(transformer.Id, AssetCondition.Poor, "Tank corrosion"));

        clock.Advance(TimeSpan.FromSeconds(1));
        var spare = await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer("ABB-2")));

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.WithAssetsAsync(assets => assets.AssessConditionAsync(spare.Id, AssetCondition.Poor, "Shelf-worn"));

        var wanted = await host.WithAssetsAsync(assets => assets.ListAsync(
            new AssetQuery(Status: AssetStatus.InService, Condition: AssetCondition.Poor)));

        Assert.Equal(transformer.Id, Assert.Single(wanted).Id);
    }

    [Fact]
    public async Task The_list_is_filtered_by_class() =>
        Assert.Equal(AssetClass.Pole, (await ListedByClassAsync()).Class);

    private static async Task<Features.Assets.Asset> ListedByClassAsync()
    {
        using var host = Host(out var clock);

        await host.WithAssetsAsync(assets => assets.RegisterAsync(Transformer("ABB-1")));

        clock.Advance(TimeSpan.FromSeconds(1));

        await host.WithAssetsAsync(assets => assets.RegisterAsync(
            new RegisterAssetInput(AssetClass.Pole, "Pole R-0472, As Nieves Road")));

        return Assert.Single(await host.WithAssetsAsync(assets => assets.ListAsync(new AssetQuery(Class: AssetClass.Pole))));
    }

    [Fact]
    public async Task The_page_size_is_clamped_however_much_a_caller_asks_for()
    {
        using var host = Host(out _);

        var everything = await host.WithAssetsAsync(assets => assets.ListAsync(new AssetQuery(Limit: int.MaxValue)));

        Assert.True(everything.Count <= AssetService.MaxPageSize);
    }
}
