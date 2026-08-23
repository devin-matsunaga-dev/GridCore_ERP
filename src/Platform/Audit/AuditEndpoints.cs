using GridCore.Platform.Data;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Platform.Audit;

/// <summary>One audit entry as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="OccurredAt">When the action happened.</param>
/// <param name="UserId">Who did it.</param>
/// <param name="UserName">Their display name at the time.</param>
/// <param name="Action">What they did.</param>
/// <param name="EntityType">The kind of entity acted on.</param>
/// <param name="EntityId">Which one.</param>
/// <param name="Before">Snapshot before, as raw JSON.</param>
/// <param name="After">Snapshot after, as raw JSON.</param>
public sealed record AuditEntryResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    string UserId,
    string? UserName,
    string Action,
    string EntityType,
    string EntityId,
    string? Before,
    string? After)
{
    /// <summary>Projects an <see cref="AuditEntry"/> for the wire.</summary>
    public static AuditEntryResponse From(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new AuditEntryResponse(
            entry.Id,
            entry.OccurredAt,
            entry.UserId,
            entry.UserName,
            entry.Action,
            entry.EntityType,
            entry.EntityId,
            entry.BeforeJson,
            entry.AfterJson);
    }
}

/// <summary>Read-only HTTP surface over the audit trail. The rich views are WP-4.4's.</summary>
public static class AuditEndpoints
{
    /// <summary>Route of the audit trail.</summary>
    public const string Route = "/api/audit";

    /// <summary>Largest page returned, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Maps the audit trail endpoint.</summary>
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet(Route, async (
                string? entityType,
                string? entityId,
                string? userId,
                string? action,
                int? limit,
                [FromServices] PlatformDbContext database,
                CancellationToken cancellationToken) =>
            {
                var query = database.AuditEntries.AsNoTracking();

                if (!string.IsNullOrWhiteSpace(entityType))
                {
                    query = query.Where(entry => entry.EntityType == entityType);
                }

                if (!string.IsNullOrWhiteSpace(entityId))
                {
                    query = query.Where(entry => entry.EntityId == entityId);
                }

                if (!string.IsNullOrWhiteSpace(userId))
                {
                    query = query.Where(entry => entry.UserId == userId);
                }

                if (!string.IsNullOrWhiteSpace(action))
                {
                    query = query.Where(entry => entry.Action == action);
                }

                // Ordered by key: ids are Guid v7, so the primary-key index already orders the
                // trail chronologically, newest first.
                var entries = await query
                    .OrderByDescending(entry => entry.Id)
                    .Take(Math.Clamp(limit ?? 50, 1, MaxPageSize))
                    .ToListAsync(cancellationToken);

                return Results.Ok(entries.Select(AuditEntryResponse.From).ToList());
            })
            .RequirePermission(Permissions.Platform.AuditRead)
            .WithName("ReadAuditTrail")
            .WithTags("Audit");

        return endpoints;
    }
}
