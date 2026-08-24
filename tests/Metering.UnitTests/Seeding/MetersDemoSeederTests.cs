using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Shared;
using GridCore.Modules.Metering.Seeding;
using GridCore.Modules.Metering.UnitTests.Infrastructure;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Metering.UnitTests.Seeding;

/// <summary>
/// The demo meter register. Seeded through the real aggregate, so these assertions are also a check
/// that the demo world is one the domain rules actually permit.
/// </summary>
public sealed class MetersDemoSeederTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The premises <c>CustomersDemoSeeder</c> commits, as this module is allowed to see them.</summary>
    private static FakeServiceLocationDirectory SeededPremises()
    {
        var directory = new FakeServiceLocationDirectory();

        for (var ordinal = 1; ordinal <= 10; ordinal++)
        {
            directory.Add($"L-{ordinal:D6}");
        }

        return directory;
    }

    private static async Task<List<Meter>> SeededAsync(MeteringTestHost host, FakeServiceLocationDirectory premises)
    {
        await using (var write = host.NewMeteringContext())
        {
            await new MetersDemoSeeder(write, premises, new FakeClock(Now)).SeedAsync(CancellationToken.None);

            // The seeder itself never saves — the runner's unit of work does. Here the test plays
            // that part, which is also what proves the seeder left a saveable graph behind.
            await write.SaveChangesAsync();
        }

        await using var read = host.NewMeteringContext();

        return await read.Meters
            .Include(meter => meter.History)
            .OrderBy(meter => meter.MeterNumber)
            .ToListAsync();
    }

    [Fact]
    public void The_seeder_is_named_and_ordered_after_the_customer_registries()
    {
        IDemoSeeder seeder = new MetersDemoSeeder(null!, null!, TimeProvider.System);

        // The name is the dedupe key and is never renamed — a rename seeds a second register.
        Assert.Equal("metering.meters", seeder.Name);
        Assert.Equal(600, seeder.Order);
    }

    [Fact]
    public async Task Every_meter_type_appears_at_least_once()
    {
        using var host = new MeteringTestHost();

        var seeded = await SeededAsync(host, SeededPremises());

        Assert.Equal(Enum.GetValues<MeterType>().ToHashSet(), seeded.Select(meter => meter.Type).ToHashSet());
    }

    [Fact]
    public async Task Every_status_appears_at_least_once()
    {
        // So the register opens with every pill on screen rather than eight identical ones, and so
        // a filter for any status has something behind it.
        using var host = new MeteringTestHost();

        var seeded = await SeededAsync(host, SeededPremises());

        Assert.Equal(Enum.GetValues<MeterStatus>().ToHashSet(), seeded.Select(meter => meter.Status).ToHashSet());
    }

    [Fact]
    public async Task The_numbers_run_from_one_so_the_first_real_registration_continues_the_series()
    {
        using var host = new MeteringTestHost();

        var seeded = await SeededAsync(host, SeededPremises());

        Assert.Equal("MTR-000001", seeded[0].MeterNumber);
        Assert.Equal(
            Enumerable.Range(1, seeded.Count).Select(ordinal => $"MTR-{ordinal:D6}"),
            seeded.Select(meter => meter.MeterNumber));
    }

    [Fact]
    public async Task No_premise_ends_up_with_two_meters_on_it()
    {
        // The demo world has to obey the rule the module enforces, or the first screen anybody
        // opens shows something the register says is impossible.
        using var host = new MeteringTestHost();

        var fitted = (await SeededAsync(host, SeededPremises()))
            .Select(meter => meter.ServiceLocationId)
            .OfType<Guid>()
            .ToList();

        Assert.Equal(fitted.Count, fitted.Distinct().Count());
    }

    [Fact]
    public async Task Every_fitted_meter_holds_a_premise_and_every_unfitted_one_holds_none()
    {
        using var host = new MeteringTestHost();

        var seeded = await SeededAsync(host, SeededPremises());

        Assert.All(seeded, meter => Assert.Equal(meter.IsFitted, meter.ServiceLocationId is not null));
    }

    [Fact]
    public async Task A_withdrawn_meter_still_says_which_premise_it_came_off()
    {
        // The demo world's proof that "what measured that premise" survives an exchange. The meter
        // row has let go of the premise; the history line has not.
        using var host = new MeteringTestHost();

        var withdrawn = (await SeededAsync(host, SeededPremises()))
            .Single(meter => meter.Status is MeterStatus.Removed);

        var removal = withdrawn.History.Single(entry => entry.EntryType is MeterHistoryEntryType.Removed);

        Assert.Null(withdrawn.ServiceLocationId);
        Assert.NotNull(removal.ServiceLocationId);
    }

    [Fact]
    public async Task Every_seeded_line_is_attributed_to_a_demo_actor()
    {
        // The demo: prefix is what keeps a seeded history line from ever being mistaken for one a
        // real crew made.
        using var host = new MeteringTestHost();

        var seeded = await SeededAsync(host, SeededPremises());

        Assert.All(
            seeded.SelectMany(meter => meter.History),
            entry => Assert.StartsWith("demo:", entry.ActorId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_premise_that_was_never_seeded_fails_loudly_rather_than_quietly_unmetered()
    {
        // Failure path. A demo world that silently skips half its meters because a place name was
        // edited is worse than one that refuses to start and says which row is missing.
        using var host = new MeteringTestHost();

        await using var write = host.NewMeteringContext();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MetersDemoSeeder(write, new FakeServiceLocationDirectory(), new FakeClock(Now))
                .SeedAsync(CancellationToken.None));

        Assert.Contains("L-000001", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_premise_out_of_service_is_treated_as_one_that_is_not_there()
    {
        // ListServiceableAsync only answers for premises service may be delivered to, so a
        // deactivated one cannot quietly acquire a meter.
        using var host = new MeteringTestHost();

        var premises = new FakeServiceLocationDirectory();

        for (var ordinal = 1; ordinal <= 10; ordinal++)
        {
            premises.Add($"L-{ordinal:D6}", isActive: ordinal != 1);
        }

        await using var write = host.NewMeteringContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MetersDemoSeeder(write, premises, new FakeClock(Now)).SeedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_number_generator_continues_the_series_the_seeder_left_behind()
    {
        // The reason the seeder assigns its own numbers: inside the seeding transaction its rows
        // are invisible to a query, so the generator would hand out MTR-000001 all over again.
        using var host = new MeteringTestHost();

        var seeded = await SeededAsync(host, SeededPremises());

        var next = await host.InScopeAsync(services =>
            services.GetRequiredService<IMeterNumberGenerator>().NextMeterNumberAsync());

        Assert.Equal($"MTR-{seeded.Count + 1:D6}", next);
    }
}
