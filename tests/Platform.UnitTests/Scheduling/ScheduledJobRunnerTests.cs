using GridCore.Platform.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GridCore.Platform.UnitTests.Scheduling;

public class ScheduledJobRunnerTests
{
    private sealed class CountingJob(TimeSpan interval, int signalAfter = 1) : IScheduledJob
    {
        private int _runs;

        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "counting";

        public TimeSpan Interval { get; } = interval;

        public Task Reached => _reached.Task;

        public Task RunAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _runs) >= signalAfter)
            {
                _reached.TrySetResult();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingJob : IScheduledJob
    {
        public string Name => "throwing";

        public TimeSpan Interval => TimeSpan.FromMinutes(5);

        public Task RunAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the vendor feed is down");
    }

    [Fact]
    public async Task A_job_that_throws_is_logged_and_the_schedule_survives()
    {
        var succeeded = await ScheduledJobRunner.RunOnceAsync(
            new ThrowingJob(),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.False(succeeded);
    }

    [Fact]
    public async Task A_healthy_job_reports_success()
    {
        var succeeded = await ScheduledJobRunner.RunOnceAsync(
            new CountingJob(TimeSpan.FromMinutes(1)),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.True(succeeded);
    }

    [Fact]
    public void A_non_positive_interval_is_refused_rather_than_spinning_the_loop()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => ScheduleValidation.Validate(
            [new Schedule(typeof(CountingJob), "nightly-sweep", TimeSpan.Zero)]));

        Assert.Contains("non-positive interval", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_jobs_may_not_share_a_name()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => ScheduleValidation.Validate(
        [
            new Schedule(typeof(CountingJob), "sweep", TimeSpan.FromMinutes(1)),
            new Schedule(typeof(ThrowingJob), "sweep", TimeSpan.FromMinutes(1)),
        ]));

        Assert.Contains("Duplicate scheduled job name", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registered_jobs_run_repeatedly_on_their_interval()
    {
        var job = new CountingJob(TimeSpan.FromMilliseconds(5), signalAfter: 3);

        await using var services = new ServiceCollection()
            .AddSingleton(job)
            .BuildServiceProvider();

        using var runner = new ScheduledJobRunner(
            services.GetRequiredService<IServiceScopeFactory>(),
            [new ScheduledJobDescriptor(typeof(CountingJob))],
            NullLogger<ScheduledJobRunner>.Instance,
            TimeProvider.System);

        await runner.StartAsync(CancellationToken.None);

        // Awaiting the job's own signal rather than sleeping — CONVENTIONS.md rule G.
        await job.Reached.WaitAsync(TimeSpan.FromSeconds(10));

        await runner.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_host_with_no_scheduled_jobs_starts_and_stops_cleanly()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();

        using var runner = new ScheduledJobRunner(
            services.GetRequiredService<IServiceScopeFactory>(),
            [],
            NullLogger<ScheduledJobRunner>.Instance,
            TimeProvider.System);

        await runner.StartAsync(CancellationToken.None);
        await runner.StopAsync(CancellationToken.None);
    }
}
