using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Assets.Data;
using GridCore.Modules.Assets.Features.Assets;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GridCore.IntegrationTests;

/// <summary>
/// The asset register against real Postgres. The fast tier proves the lifecycle, the history and
/// the guards on SQLite; what a container adds is the rules only the database can keep — the unique
/// indexes behind a race the service's own checks cannot see, and the <c>numeric(9,6)</c> column a
/// surveyed position actually lands in.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AssetRegistryTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_asset_walks_its_lifecycle_and_keeps_the_history_in_the_assets_schema()
    {
        Guid assetId;

        await using (var scope = fixture.CreateScope())
        {
            assetId = (await scope.ServiceProvider.GetRequiredService<IAssetService>()
                .RegisterAsync(new RegisterAssetInput(
                    AssetClass.Transformer,
                    "Songsong Substation Transformer T-3",
                    "ABB-T-884213",
                    "ABB",
                    "ONAN 1500 kVA",
                    new DateOnly(2009, 3, 2),
                    14.140900m,
                    145.184800m,
                    "Bay 3, east side of the switchyard"))).Id;
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAssetService>()
                .ChangeStatusAsync(assetId, AssetStatus.InService, "Energised on bay 3");
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAssetService>()
                .AssessConditionAsync(assetId, AssetCondition.Fair, "Some tank corrosion");
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAssetService>()
                .ChangeStatusAsync(assetId, AssetStatus.UnderMaintenance, "Withdrawn for gasket replacement");
        }

        await using var read = fixture.CreateScope();

        var stored = await read.ServiceProvider.GetRequiredService<AssetsDbContext>()
            .Assets.AsNoTracking()
            .Include(asset => asset.History)
            .SingleAsync(asset => asset.Id == assetId);

        Assert.Equal(AssetStatus.UnderMaintenance, stored.Status);
        Assert.Equal(AssetCondition.Fair, stored.Condition);

        // A surveyed position through numeric(9,6) and back, exact to the sixth decimal place. On a
        // float column this is where 14.140900 would come back as 14.140899999999999.
        Assert.Equal(new GeoPosition(14.140900m, 145.184800m), stored.Position);

        // Each write committed on its own request, so the history is what Postgres holds rather
        // than what one change tracker remembers.
        Assert.Equal(
            [
                AssetHistoryEntryType.Registered,
                AssetHistoryEntryType.StatusChanged,
                AssetHistoryEntryType.ConditionAssessed,
                AssetHistoryEntryType.StatusChanged,
            ],
            stored.History.OrderBy(entry => entry.Id).Select(entry => entry.EntryType).ToArray());
    }

    [Fact]
    public async Task The_database_refuses_a_second_asset_carrying_one_serial_number()
    {
        // The service checks first, so this inserts straight through the context to get past it —
        // which is exactly what a race between two registrations does. The unique index is the only
        // thing standing between that race and one physical transformer held twice on the register.
        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAssetService>()
                .RegisterAsync(new RegisterAssetInput(AssetClass.Transformer, "Songsong Transformer T-3", "ABB-T-884213"));
        }

        await using var second = fixture.CreateScope();

        var database = second.ServiceProvider.GetRequiredService<AssetsDbContext>();

        database.Assets.Add(Asset.Register(
            "AST-999999",
            AssetClass.Transformer,
            "The same transformer, registered twice",
            new RegistryActor("system", "system"),
            DateTimeOffset.UtcNow,
            serialNumber: "ABB-T-884213"));

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());

        Assert.Equal("23505", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }

    [Fact]
    public async Task Plant_carrying_no_serial_number_is_registered_as_often_as_it_stands()
    {
        // Postgres treats NULLs in a unique index as distinct — which is what lets a register hold
        // a thousand poles, and is worth proving on the provider that will actually hold them.
        foreach (var ordinal in Enumerable.Range(1, 3))
        {
            await using var scope = fixture.CreateScope();

            await scope.ServiceProvider.GetRequiredService<IAssetService>()
                .RegisterAsync(new RegisterAssetInput(AssetClass.Pole, $"Pole R-047{ordinal}, As Nieves Road"));
        }

        await using var read = fixture.CreateScope();

        Assert.Equal(
            3,
            await read.ServiceProvider.GetRequiredService<AssetsDbContext>()
                .Assets.CountAsync(asset => asset.Class == AssetClass.Pole));
    }

    [Fact]
    public async Task Tags_are_issued_in_sequence_across_separate_requests()
    {
        // The generator reads the highest committed tag inside the caller's transaction. On SQLite
        // that is one file; here it is the ORDER BY over the real unique index that has to answer.
        var issued = new List<string>();

        foreach (var ordinal in Enumerable.Range(1, 3))
        {
            await using var scope = fixture.CreateScope();

            issued.Add((await scope.ServiceProvider.GetRequiredService<IAssetService>()
                .RegisterAsync(new RegisterAssetInput(AssetClass.Pole, $"Pole R-047{ordinal}"))).AssetTag);
        }

        Assert.Equal(["AST-000001", "AST-000002", "AST-000003"], issued);
    }
}
