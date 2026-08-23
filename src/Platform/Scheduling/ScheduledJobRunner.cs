using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GridCore.Platform.Scheduling;

/// <summary>A job type the host has been asked to run on a schedule.</summary>
/// <param name="JobType">The <see cref="IScheduledJob"/> implementation, resolved per run from a fresh DI scope.</param>
public sealed record ScheduledJobDescriptor(Type JobType);

/// <summary>What one job's schedule looks like once its metadata has been read.</summary>
/// <param name="JobType">The implementation type.</param>
/// <param name="Name">Its <see cref="IScheduledJob.Name"/>.</param>
/// <param name="Interval">Its <see cref="IScheduledJob.Interval"/>.</param>
public sealed record Schedule(Type JobType, string Name, TimeSpan Interval);

/// <summary>
/// The schedule rules, pure so they can be read and tested without a host: names identify a
/// schedule in the logs and must be unique, and a non-positive interval would spin the loop.
/// </summary>
public static class ScheduleValidation
{
    /// <summary>Throws if the set of schedules is not runnable.</summary>
    /// <exception cref="InvalidOperationException">An interval is not positive, or a name is used twice.</exception>
    public static IReadOnlyList<Schedule> Validate(IEnumerable<Schedule> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);

        var list = schedules.ToList();

        var invalid = list.Find(schedule => schedule.Interval <= TimeSpan.Zero);

        if (invalid is not null)
        {
            throw new InvalidOperationException(
                $"Scheduled job '{invalid.Name}' has a non-positive interval of {invalid.Interval}; it would spin.");
        }

        var duplicate = list
            .GroupBy(schedule => schedule.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        return duplicate is null
            ? list
            : throw new InvalidOperationException(
                $"Duplicate scheduled job name '{duplicate.Key}'. Job names identify a schedule in the logs and must be unique.");
    }
}

/// <summary>
/// Drives every registered <see cref="IScheduledJob"/> on its own interval. Each job gets its own
/// loop, so a slow or failing job never delays another, and each run gets its own DI scope, so a
/// job can take scoped dependencies such as a DbContext.
/// </summary>
public sealed partial class ScheduledJobRunner(
    IServiceScopeFactory scopeFactory,
    IEnumerable<ScheduledJobDescriptor> descriptors,
    ILogger<ScheduledJobRunner> logger,
    TimeProvider clock) : BackgroundService
{
    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var schedules = ScheduleValidation.Validate(ReadSchedules());

        return schedules.Count is 0
            ? Task.CompletedTask
            : Task.WhenAll(schedules.Select(schedule => LoopAsync(schedule, stoppingToken)));
    }

    /// <summary>
    /// Runs one pass of a job and reports whether it succeeded. A job that throws is logged and
    /// swallowed: one broken sweep must not take the host down or stop the other schedules.
    /// </summary>
    internal static async Task<bool> RunOnceAsync(IScheduledJob job, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        try
        {
            await job.RunAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a failure.
            return false;
        }
        catch (Exception exception)
        {
            JobFailed(logger, job.Name, exception);

            return false;
        }
    }

    private IReadOnlyList<Schedule> ReadSchedules()
    {
        using var scope = scopeFactory.CreateScope();

        return descriptors
            .Select(descriptor => (IScheduledJob)scope.ServiceProvider.GetRequiredService(descriptor.JobType))
            .Select(job => new Schedule(job.GetType(), job.Name, job.Interval))
            .ToList();
    }

    private async Task LoopAsync(Schedule schedule, CancellationToken stoppingToken)
    {
        // Wait one interval before the first pass, so nothing fires while the host is still booting
        // and every job does not stampede at once.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(schedule.Interval, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            using var scope = scopeFactory.CreateScope();

            var job = (IScheduledJob)scope.ServiceProvider.GetRequiredService(schedule.JobType);

            await RunOnceAsync(job, logger, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Error,
        Message = "Scheduled job {JobName} failed; the schedule continues.")]
    private static partial void JobFailed(ILogger logger, string jobName, Exception exception);
}
