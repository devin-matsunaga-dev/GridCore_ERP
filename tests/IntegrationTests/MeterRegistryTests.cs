using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Metering.Data;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GridCore.IntegrationTests;

/// <summary>
/// The meter register against real Postgres, and — the reason this class exists rather than more
/// fast-tier cases — GridCore's first <b>cross-module read</b> running for real.
/// </summary>
/// <remarks>
/// The fast tier proves the lifecycle and the guards with a fake premise directory. What only a
/// container can show is the two modules meeting: Metering asking the real
/// <see cref="IServiceLocationDirectory"/>, answered by Customers out of the customers schema, on
/// the same connection and inside the same transaction as the metering write — with two separate
/// schemas' unique indexes standing behind both rules.
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MeterRegistryTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> APremiseAsync(string line1 = "128 As Nieves Road", bool isActive = true)
    {
        await using var scope = fixture.CreateScope();

        var registered = await scope.ServiceProvider.GetRequiredService<IServiceLocationService>()
            .RegisterAsync(new ServiceLocationInput(
                Address.Create(line1, "Songsong", "Rota", "MP", postalCode: "96951"),
                "Meter on the north wall",
                isActive,
                isActive ? null : "Demolished"));

        return registered.Id;
    }

    private async Task<Guid> AMeterAsync(string serialNumber)
    {
        await using var scope = fixture.CreateScope();

        return (await scope.ServiceProvider.GetRequiredService<IMeterService>()
            .RegisterAsync(new RegisterMeterInput(serialNumber, MeterType.SinglePhase, "Sensus", "iConA")))
            .Meter.Id;
    }

    [Fact]
    public async Task A_meter_is_fitted_to_a_premise_the_customers_module_owns_and_the_two_schemas_agree()
    {
        var premise = await APremiseAsync();
        var meter = await AMeterAsync("SEN-4471102");

        await using (var scope = fixture.CreateScope())
        {
            var fitted = await scope.ServiceProvider.GetRequiredService<IMeterService>()
                .AssignAsync(meter, new AssignMeterInput(premise, 14_820.500m, "Transfer of service"));

            // The premise came back through the seam, resolved from the customers schema by the
            // module that owns it — no join, and Metering never named a table it does not own.
            Assert.Equal("L-000001", fitted.ServiceLocation?.LocationCode);
        }

        await using var read = fixture.CreateScope();

        var stored = await read.ServiceProvider.GetRequiredService<MeteringDbContext>()
            .Meters.AsNoTracking()
            .Include(candidate => candidate.History)
            .SingleAsync(candidate => candidate.Id == meter);

        Assert.Equal(MeterStatus.Installed, stored.Status);
        Assert.Equal(premise, stored.ServiceLocationId);

        // A dial reading through numeric(18,3) and back, exact. On a float column this is where
        // 14820.500 would come back as 14820.499999999998.
        Assert.Equal(14_820.500m, stored.InstallationReading);

        Assert.Equal(
            [MeterHistoryEntryType.Registered, MeterHistoryEntryType.Installed],
            stored.History.OrderBy(entry => entry.Id).Select(entry => entry.EntryType).ToArray());
    }

    [Fact]
    public async Task The_database_refuses_a_second_meter_on_one_premise()
    {
        // WP-2.1's headline rule where it is actually guaranteed. The service checks first, so this
        // inserts straight through the context to get past it — which is exactly what a race
        // between two crews booking their work does.
        var premise = await APremiseAsync();
        var meter = await AMeterAsync("SEN-4471102");

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterService>()
                .AssignAsync(meter, new AssignMeterInput(premise));
        }

        await using var second = fixture.CreateScope();

        var database = second.ServiceProvider.GetRequiredService<MeteringDbContext>();

        var interloper = Meter.Register(
            "MTR-999999",
            "SEN-9999999",
            MeterType.SinglePhase,
            new RegistryActor("system", "system"),
            DateTimeOffset.UtcNow);

        interloper.InstallAt(premise, new RegistryActor("system", "system"), DateTimeOffset.UtcNow);

        database.Meters.Add(interloper);

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());

        Assert.Equal("23505", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }

    [Fact]
    public async Task Any_number_of_meters_sit_in_a_store_together()
    {
        // The other half of the same unfiltered unique index: an unfitted meter holds NULL, and
        // Postgres treats NULLs in a unique index as distinct. Worth proving on the provider that
        // will actually hold them, because the rule rests entirely on that behaviour.
        foreach (var ordinal in Enumerable.Range(1, 3))
        {
            await AMeterAsync($"SEN-447110{ordinal}");
        }

        await using var read = fixture.CreateScope();

        Assert.Equal(
            3,
            await read.ServiceProvider.GetRequiredService<MeteringDbContext>()
                .Meters.CountAsync(meter => meter.Status == MeterStatus.InStore));
    }

    [Fact]
    public async Task One_meter_per_premise_and_one_open_account_per_premise_are_separate_rules()
    {
        // The owner's call for WP-2.1, proven where both rules actually live: two unique indexes in
        // two schemas that know nothing about each other. A premise can be metered with no account
        // open on it at all — a new build's supply is live and measured before anybody is billed.
        var premise = await APremiseAsync();
        var meter = await AMeterAsync("SEN-4471102");

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterService>()
                .AssignAsync(meter, new AssignMeterInput(premise));
        }

        await using var read = fixture.CreateScope();

        var fitted = await read.ServiceProvider.GetRequiredService<MeteringDbContext>()
            .Meters.AsNoTracking()
            .SingleAsync(candidate => candidate.ServiceLocationId == premise);

        Assert.Equal(MeterStatus.Installed, fitted.Status);
    }

    [Fact]
    public async Task A_premise_the_customers_module_does_not_know_is_refused()
    {
        // Failure path across the boundary. Nothing in the metering schema could catch this — the
        // column has no foreign key precisely because the row is in another module's schema — so
        // the directory is the only thing standing between a typo and a meter fitted to nowhere.
        var meter = await AMeterAsync("SEN-4471102");

        await using var scope = fixture.CreateScope();

        await Assert.ThrowsAsync<ServiceLocationNotFoundException>(() =>
            scope.ServiceProvider.GetRequiredService<IMeterService>()
                .AssignAsync(meter, new AssignMeterInput(Guid.CreateVersion7())));
    }

    [Fact]
    public async Task A_premise_that_is_out_of_service_is_refused()
    {
        var premise = await APremiseAsync("87 Airport Road", isActive: false);
        var meter = await AMeterAsync("SEN-4471102");

        await using var scope = fixture.CreateScope();

        await Assert.ThrowsAsync<MeterWorkflowException>(() =>
            scope.ServiceProvider.GetRequiredService<IMeterService>()
                .AssignAsync(meter, new AssignMeterInput(premise)));
    }

    [Fact]
    public async Task A_premise_is_metered_again_once_the_meter_on_it_comes_off()
    {
        var premise = await APremiseAsync();
        var first = await AMeterAsync("SEN-4471102");
        var second = await AMeterAsync("SEN-4471188");

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterService>()
                .AssignAsync(first, new AssignMeterInput(premise));
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterService>()
                .RemoveAsync(first, "Exchanged for a fault");
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeterService>()
                .AssignAsync(second, new AssignMeterInput(premise, 0m, "Exchange meter fitted"));
        }

        await using var read = fixture.CreateScope();

        var meters = read.ServiceProvider.GetRequiredService<MeteringDbContext>().Meters.AsNoTracking();

        Assert.Equal(second, (await meters.SingleAsync(meter => meter.ServiceLocationId == premise)).Id);

        // And the withdrawn meter still says which premise it came off, which is what keeps a bill
        // dispute over the period before the exchange answerable.
        var history = read.ServiceProvider.GetRequiredService<MeteringDbContext>().MeterHistory.AsNoTracking();

        Assert.Equal(
            premise,
            (await history.SingleAsync(entry =>
                entry.MeterId == first && entry.EntryType == MeterHistoryEntryType.Removed)).ServiceLocationId);
    }

    [Fact]
    public async Task Meter_numbers_are_issued_in_sequence_across_separate_requests()
    {
        // The generator reads the highest committed number inside the caller's transaction. On
        // SQLite that is one file; here it is the ORDER BY over the real unique index that answers.
        var issued = new List<string>();

        foreach (var ordinal in Enumerable.Range(1, 3))
        {
            await using var scope = fixture.CreateScope();

            issued.Add((await scope.ServiceProvider.GetRequiredService<IMeterService>()
                .RegisterAsync(new RegisterMeterInput($"SEN-447110{ordinal}", MeterType.SinglePhase)))
                .Meter.MeterNumber);
        }

        Assert.Equal(["MTR-000001", "MTR-000002", "MTR-000003"], issued);
    }
}
