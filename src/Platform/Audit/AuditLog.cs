using System.Diagnostics;
using GridCore.Platform.Data;
using GridCore.Platform.Security;

namespace GridCore.Platform.Audit;

/// <summary>
/// Writes the audit trail into the platform schema, attributing every entry to
/// <see cref="ICurrentUser"/> rather than to claims read at the call site.
/// </summary>
public sealed class AuditLog(PlatformDbContext database, ICurrentUser currentUser, TimeProvider clock) : IAuditLog
{
    /// <inheritdoc />
    public AuditEntry Record(
        string action,
        string entityType,
        string entityId,
        object? before = null,
        object? after = null)
    {
        var entry = AuditEntry.For(
            clock.GetUtcNow(),
            currentUser.UserId,
            currentUser.UserName,
            action,
            entityType,
            entityId,
            before,
            after,
            Activity.Current?.Id);

        database.AuditEntries.Add(entry);

        return entry;
    }

    /// <inheritdoc />
    public async Task<AuditEntry> RecordAsync(
        string action,
        string entityType,
        string entityId,
        object? before = null,
        object? after = null,
        CancellationToken cancellationToken = default)
    {
        var entry = Record(action, entityType, entityId, before, after);

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return entry;
    }
}
