using System.Text.Json;
using System.Text.Json.Serialization;

namespace GridCore.Platform.Audit;

/// <summary>
/// One line of the append-only audit trail: who did what to which entity, and what the entity
/// looked like before and after. Written by <see cref="IAuditLog"/>; never updated or deleted —
/// <see cref="Data.PlatformDbContext"/> refuses to save a modified or removed entry.
/// </summary>
public sealed class AuditEntry
{
    /// <summary>Longest value the trail stores for a name-like column; longer values are truncated, never rejected.</summary>
    public const int NameLength = 256;

    private AuditEntry()
    {
        // EF materialisation.
        UserId = string.Empty;
        Action = string.Empty;
        EntityType = string.Empty;
        EntityId = string.Empty;
    }

    /// <summary>Identifier of this entry. Guid v7, so the trail is chronologically ordered by key.</summary>
    public Guid Id { get; private init; }

    /// <summary>When the audited action happened.</summary>
    public DateTimeOffset OccurredAt { get; private init; }

    /// <summary>Identity-provider subject id of the actor, or <c>system</c> for background work.</summary>
    public string UserId { get; private init; }

    /// <summary>Display name of the actor at the time of the action.</summary>
    public string? UserName { get; private init; }

    /// <summary>What was done, e.g. <c>approval.approve</c>. See <see cref="AuditActions"/>.</summary>
    public string Action { get; private init; }

    /// <summary>The kind of entity acted on, e.g. <c>platform.approval_request</c>.</summary>
    public string EntityType { get; private init; }

    /// <summary>Identifier of the entity acted on, as text so any key type fits.</summary>
    public string EntityId { get; private init; }

    /// <summary>JSON snapshot of the entity before the action; <see langword="null"/> for a creation.</summary>
    public string? BeforeJson { get; private init; }

    /// <summary>JSON snapshot of the entity after the action; <see langword="null"/> for a deletion.</summary>
    public string? AfterJson { get; private init; }

    /// <summary>Trace identifier of the request that caused the action, when there was one.</summary>
    public string? CorrelationId { get; private init; }

    /// <summary>Builds an entry. Snapshots are serialised here so callers pass plain objects.</summary>
    public static AuditEntry For(
        DateTimeOffset occurredAt,
        string userId,
        string? userName,
        string action,
        string entityType,
        string entityId,
        object? before,
        object? after,
        string? correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        return new AuditEntry
        {
            Id = Guid.CreateVersion7(occurredAt),
            OccurredAt = occurredAt,
            UserId = Truncate(userId),
            UserName = TruncateOptional(userName),
            Action = Truncate(action),
            EntityType = Truncate(entityType),
            EntityId = Truncate(entityId),
            BeforeJson = Snapshot(before),
            AfterJson = Snapshot(after),
            CorrelationId = TruncateOptional(correlationId),
        };
    }

    /// <summary>Serialises a before/after snapshot. Kept here so the trail has one JSON shape.</summary>
    public static string? Snapshot(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, value.GetType(), AuditJson.Options);

    private static string Truncate(string value) =>
        value.Length > NameLength ? value[..NameLength] : value;

    private static string? TruncateOptional(string? value) =>
        value is null ? null : Truncate(value);
}

/// <summary>The JSON shape of audit snapshots. One place, so old entries stay readable.</summary>
public static class AuditJson
{
    /// <summary>
    /// camelCase, no indentation, enums by name. The names matter: an entry written today is read
    /// years later, and a bare <c>2</c> would mean whatever the enum happens to say by then.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };
}
