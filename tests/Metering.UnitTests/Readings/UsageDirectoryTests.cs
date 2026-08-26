using GridCore.Contracts.Directories;
using GridCore.Contracts.Services;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Modules.Metering.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Metering.UnitTests.Readings;

/// <summary>
/// What premises consume, as the rest of GridCore reads it (WP-2.17) — the seam Customers assesses
/// a usage-based deposit through, and the reason it never learns a <c>metering</c> schema exists.
/// </summary>
/// <remarks>
/// These prove which readings COUNT; <see cref="UsageAverageTests"/> proves what they come to. The
/// split is why neither file needs the other's setup.
/// </remarks>
public class UsageDirectoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_premise_nobody_has_read_has_no_history_rather_than_no_usage()
    {
        using var host = NewHost();

        var (meter, premise) = await host.FitMeterAsync("SN-USE-1", installationReading: 1_000m);

        _ = meter;

        var usage = await WithUsageAsync(host, directory =>
            directory.AverageMonthlyAtLocationAsync(premise, ServiceType.Electricity, 2));

        // A fitted meter with no reading taken off it yet. Null, not zero — the distinction a
        // deposit rule falls back to its minimum on.
        Assert.False(usage.HasHistory);
        Assert.Null(usage.AverageMonthlyUsage);
        Assert.Equal(0, usage.PeriodsConsidered);
    }

    [Fact]
    public async Task A_premise_that_was_never_metered_at_all_has_no_history()
    {
        using var host = NewHost();

        var usage = await WithUsageAsync(host, directory =>
            directory.AverageMonthlyAtLocationAsync(Guid.CreateVersion7(Now), ServiceType.Electricity, 2));

        Assert.False(usage.HasHistory);
    }

    [Fact]
    public async Task Two_monthly_reads_average_to_a_month_of_them()
    {
        var clock = new FakeClock(Now.AddDays(-90));

        using var host = NewHost(clock);

        var (meter, premise) = await host.FitMeterAsync("SN-USE-2", installationReading: 1_000m);

        await RecordAsync(host, clock, meter.Meter.Id, 1_300m);
        await RecordAsync(host, clock, meter.Meter.Id, 1_600m);

        var usage = await WithUsageAsync(host, directory =>
            directory.AverageMonthlyAtLocationAsync(premise, ServiceType.Electricity, 6));

        Assert.True(usage.HasHistory);
        Assert.Equal(2, usage.PeriodsConsidered);

        // 600 units over 60 days, scaled to a 30.4375-day month.
        Assert.Equal(304.375m, usage.AverageMonthlyUsage);
        Assert.Equal(60, usage.DaysCovered);
    }

    [Fact]
    public async Task A_service_this_deployment_does_not_meter_answers_with_no_history()
    {
        // Every meter in the register is an electricity meter, so answering a question about this
        // premise's WATER with its kWh would hand a caller one unit labelled as another. No history
        // is the true answer, and it is the one that makes a deposit fall back to its floor.
        var clock = new FakeClock(Now.AddDays(-60));

        using var host = NewHost(clock);

        var (meter, premise) = await host.FitMeterAsync("SN-USE-3", installationReading: 1_000m);

        await RecordAsync(host, clock, meter.Meter.Id, 1_400m);

        Assert.True((await WithUsageAsync(host, directory =>
            directory.AverageMonthlyAtLocationAsync(premise, ServiceType.Electricity, 6))).HasHistory);

        Assert.False((await WithUsageAsync(host, directory =>
            directory.AverageMonthlyAtLocationAsync(premise, ServiceType.Water, 6))).HasHistory);

        Assert.False((await WithUsageAsync(host, directory =>
            directory.AverageMonthlyAtLocationAsync(premise, ServiceType.Wastewater, 6))).HasHistory);
    }

    [Fact]
    public async Task A_missing_read_is_skipped_rather_than_counted_as_a_month_of_nothing()
    {
        var clock = new FakeClock(Now.AddDays(-90));

        using var host = NewHost(clock);

        var (meter, premise) = await host.FitMeterAsync("SN-USE-4", installationReading: 1_000m);

        await RecordAsync(host, clock, meter.Meter.Id, 1_300m);

        // A read nobody could take. It carries no consumption figure, so it is not evidence that the
        // premise used nothing that month — counting it as zero would halve the average.
        clock.Advance(TimeSpan.FromDays(30));

        await host.WithReadingsAsync(readings => readings.RecordAsync(meter.Meter.Id, new RecordReadingInput(null)));

        var usage = await WithUsageAsync(host, directory =>
            directory.AverageMonthlyAtLocationAsync(premise, ServiceType.Electricity, 6));

        Assert.Equal(1, usage.PeriodsConsidered);
        Assert.Equal(304.375m, usage.AverageMonthlyUsage);
    }

    [Fact]
    public async Task The_period_a_premise_covers_is_reported_beside_the_average()
    {
        // "Two months of average usage" is a claim a rep has to defend at the counter, and the span
        // it was drawn from is the evidence for it.
        var clock = new FakeClock(Now.AddDays(-90));

        using var host = NewHost(clock);

        var (meter, premise) = await host.FitMeterAsync("SN-USE-5", installationReading: 1_000m);

        await RecordAsync(host, clock, meter.Meter.Id, 1_300m);
        await RecordAsync(host, clock, meter.Meter.Id, 1_600m);

        var usage = await WithUsageAsync(host, directory =>
            directory.AverageMonthlyAtLocationAsync(premise, ServiceType.Electricity, 6));

        Assert.NotNull(usage.FirstPeriodStart);
        Assert.NotNull(usage.LastPeriodEnd);
        Assert.True(usage.FirstPeriodStart < usage.LastPeriodEnd);
        Assert.Equal(60, usage.DaysCovered);
    }

    [Fact]
    public async Task The_cap_takes_the_most_recent_periods_rather_than_the_oldest()
    {
        // A deposit is assessed on what the premise uses NOW. Three months of 300 followed by one of
        // 900: capped at one period, the answer has to be the 900.
        var clock = new FakeClock(Now.AddDays(-150));

        using var host = NewHost(clock);

        var (meter, premise) = await host.FitMeterAsync("SN-USE-6", installationReading: 1_000m);

        await RecordAsync(host, clock, meter.Meter.Id, 1_300m);
        await RecordAsync(host, clock, meter.Meter.Id, 1_600m);
        await RecordAsync(host, clock, meter.Meter.Id, 1_900m);
        await RecordAsync(host, clock, meter.Meter.Id, 2_800m);

        var usage = await WithUsageAsync(host, directory =>
            directory.AverageMonthlyAtLocationAsync(premise, ServiceType.Electricity, 1));

        Assert.Equal(1, usage.PeriodsConsidered);
        Assert.Equal(913.125m, usage.AverageMonthlyUsage);
    }

    private static MeteringTestHost NewHost(FakeClock? clock = null) =>
        new(clock ?? new FakeClock(Now), new FakeCurrentUser("auth0|reader", "A meter reader"));

    private static Task<TResult> WithUsageAsync<TResult>(
        MeteringTestHost host,
        Func<IUsageDirectory, Task<TResult>> work) =>
        host.InScopeAsync(services => work(services.GetRequiredService<IUsageDirectory>()));

    /// <summary>Moves the clock a month on and records <paramref name="reading"/>.</summary>
    private static async Task RecordAsync(MeteringTestHost host, FakeClock clock, Guid meterId, decimal reading)
    {
        clock.Advance(TimeSpan.FromDays(30));

        await host.WithReadingsAsync(readings => readings.RecordAsync(meterId, new RecordReadingInput(reading)));
    }
}
