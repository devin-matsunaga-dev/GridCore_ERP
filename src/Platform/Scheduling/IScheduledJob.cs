namespace GridCore.Platform.Scheduling;

/// <summary>
/// Recurring background work owned by a module — a billing cycle sweep, an outbox retry, a stale
/// reading check. Implementations are registered with
/// <c>AddScheduledJob&lt;TJob&gt;()</c> and driven by
/// <see cref="ScheduledJobRunner"/>; they never schedule themselves.
/// </summary>
public interface IScheduledJob
{
    /// <summary>Stable name, unique across the host. Used in logs and to reject duplicate registrations.</summary>
    string Name { get; }

    /// <summary>How long to wait between runs. Must be positive.</summary>
    TimeSpan Interval { get; }

    /// <summary>
    /// Does one pass. Runs as <see cref="Security.SystemUser"/>, so anything it writes is audited
    /// against <c>system</c>. Throwing is survivable: the runner logs it and keeps the schedule.
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken);
}
