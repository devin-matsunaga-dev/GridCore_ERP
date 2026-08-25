using GridCore.Contracts.Directories;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Metering.UnitTests.Registry;

/// <summary>
/// The meter register as the rest of GridCore reads it — GridCore's fifth cross-module read seam,
/// and the one the CSR search box (WP-2.9) turns a quoted meter number into a premise through.
/// </summary>
/// <remarks>
/// Metering could not answer "whose meter is this" without reading the customers schema, which is
/// exactly what neither module may do — so this hands over the premise and Customers takes it from
/// there. The rules pinned here are the ones Customers' <c>FakeMeterDirectory</c> stands in for, so
/// a double that drifted from them would let the search service pass in the fast tier and fail
/// against Postgres.
/// </remarks>
public class MeterDirectoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private static MeteringTestHost NewHost() => new(new FakeClock(Now), new FakeCurrentUser("auth0|reader", "A meter reader"));

    private static Task<TResult> WithDirectoryAsync<TResult>(
        MeteringTestHost host,
        Func<IMeterDirectory, Task<TResult>> work) =>
        host.InScopeAsync(services => work(services.GetRequiredService<IMeterDirectory>()));

    [Fact]
    public async Task A_meter_is_found_by_the_number_stamped_on_its_plate()
    {
        using var host = NewHost();

        var (meter, premise) = await host.FitMeterAsync("SN-DIR-1");

        var summary = await WithDirectoryAsync(host, directory => directory.FindByNumberAsync(meter.Meter.MeterNumber));

        Assert.NotNull(summary);
        Assert.Equal(meter.Meter.Id, summary.Id);
        Assert.Equal(meter.Meter.MeterNumber, summary.MeterNumber);
        Assert.Equal("SN-DIR-1", summary.SerialNumber);
        Assert.Equal(premise, summary.ServiceLocationId);
        Assert.True(summary.IsFitted);
    }

    [Fact]
    public async Task A_number_is_matched_without_regard_to_case_or_surrounding_space()
    {
        using var host = NewHost();

        var (meter, _) = await host.FitMeterAsync("SN-DIR-2");

        var summary = await WithDirectoryAsync(host, directory =>
            directory.FindByNumberAsync($"  {meter.Meter.MeterNumber.ToLowerInvariant()}  "));

        Assert.NotNull(summary);
        Assert.Equal(meter.Meter.Id, summary.Id);
    }

    [Fact]
    public async Task A_number_nobody_has_finds_nothing_rather_than_throwing() =>
        Assert.Null(await WithDirectoryAsync(NewHost(), directory => directory.FindByNumberAsync("MTR-999999")));

    [Fact]
    public async Task An_exact_lookup_will_not_answer_a_partial_number()
    {
        // The whole point of the method being separate: equality is a seek on
        // ux_meters_meter_number, and a containment that quietly crept in here would turn the
        // fifty-times-a-day lookup into a scan without anybody noticing.
        using var host = NewHost();

        var (meter, _) = await host.FitMeterAsync("SN-DIR-3");

        Assert.Null(await WithDirectoryAsync(host, directory =>
            directory.FindByNumberAsync(meter.Meter.MeterNumber[..^2])));
    }

    [Fact]
    public async Task A_half_remembered_number_comes_back_from_the_search()
    {
        using var host = NewHost();

        var (first, _) = await host.FitMeterAsync("SN-DIR-4");
        var (second, _) = await host.FitMeterAsync("SN-DIR-5");

        var found = await WithDirectoryAsync(host, directory => directory.SearchByNumberAsync("MTR-", 50));

        Assert.Contains(found, meter => meter.Id == first.Meter.Id);
        Assert.Contains(found, meter => meter.Id == second.Meter.Id);
    }

    [Fact]
    public async Task A_search_is_capped_at_the_limit_the_caller_asked_for()
    {
        using var host = NewHost();

        await host.FitMeterAsync("SN-DIR-6");
        await host.FitMeterAsync("SN-DIR-7");
        await host.FitMeterAsync("SN-DIR-8");

        Assert.Equal(2, (await WithDirectoryAsync(host, directory => directory.SearchByNumberAsync("MTR-", 2))).Count);
    }

    [Fact]
    public async Task A_meter_in_the_store_crosses_the_boundary_with_no_premise_on_it()
    {
        // A meter that is not on a wall measures nobody, and the caller is told so rather than being
        // handed a premise it would then fail to resolve.
        using var host = NewHost();

        var meter = await host.RegisterMeterAsync("SN-DIR-9");

        var summary = await WithDirectoryAsync(host, directory => directory.FindByNumberAsync(meter.Meter.MeterNumber));

        Assert.NotNull(summary);
        Assert.Null(summary.ServiceLocationId);
        Assert.False(summary.IsFitted);
    }

    [Fact]
    public async Task A_type_and_a_status_cross_the_boundary_by_name_not_as_enums()
    {
        // Contracts takes no dependency on this module's types — the rule the other four seams follow.
        using var host = NewHost();

        var meter = await host.RegisterMeterAsync("SN-DIR-10");

        var summary = await WithDirectoryAsync(host, directory => directory.FindByNumberAsync(meter.Meter.MeterNumber));

        Assert.NotNull(summary);
        Assert.Equal(MeterType.SinglePhase.ToString(), summary.Type);
        Assert.Equal(MeterStatus.InStore.ToString(), summary.Status);
    }

    [Fact]
    public async Task A_blank_number_is_refused_rather_than_matching_everything()
    {
        // The failure path. A caller that reached here with an empty box would otherwise be handed
        // the entire register, or the first row of it, as though it were an answer.
        using var host = NewHost();

        await host.FitMeterAsync("SN-DIR-11");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            WithDirectoryAsync(host, directory => directory.FindByNumberAsync("   ")));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            WithDirectoryAsync(host, directory => directory.SearchByNumberAsync("   ", 50)));
    }
}
