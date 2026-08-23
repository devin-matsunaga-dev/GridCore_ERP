namespace GridCore.Platform.Audit;

/// <summary>
/// The one-line audit helper. Every write endpoint records what it did through this — invariant 1
/// of ARCHITECTURE.md. The actor and the timestamp are supplied by the platform, so a caller only
/// says what happened.
/// </summary>
public interface IAuditLog
{
    /// <summary>
    /// Adds an entry to the current platform unit of work. Use this when the audited write is
    /// itself in <see cref="Data.PlatformDbContext"/>: the entry then commits in the same
    /// transaction as the change it describes, so a saved change can never be unaudited.
    /// </summary>
    AuditEntry Record(
        string action,
        string entityType,
        string entityId,
        object? before = null,
        object? after = null);

    /// <summary>
    /// Adds an entry and saves it immediately. Use this when the audited write happened elsewhere
    /// and there is no shared unit of work to join.
    /// </summary>
    Task<AuditEntry> RecordAsync(
        string action,
        string entityType,
        string entityId,
        object? before = null,
        object? after = null,
        CancellationToken cancellationToken = default);
}
