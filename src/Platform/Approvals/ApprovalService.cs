using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Notifications;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Platform.Approvals;

/// <summary>
/// The approval workflow over the platform schema. Each mutation audits and saves in one
/// transaction — the approval and the entry that describes it are stored together or not at all —
/// and only then tells anyone about it.
/// </summary>
public sealed class ApprovalService(
    PlatformDbContext database,
    IAuditLog audit,
    INotificationSender notifications,
    ICurrentUser currentUser,
    TimeProvider clock) : IApprovalService
{
    /// <summary>The largest page <see cref="ListAsync"/> will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public async Task<ApprovalRequest> RequestAsync(ApprovalRequestInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var request = ApprovalRequest.Raise(
            input.RequestType,
            input.SubjectType,
            input.SubjectId,
            input.RequiredPermission,
            input.Payload,
            input.Reason,
            currentUser,
            clock.GetUtcNow());

        database.ApprovalRequests.Add(request);

        audit.Record(
            AuditActions.ApprovalRequested,
            AuditEntityTypes.ApprovalRequest,
            request.Id.ToString(),
            before: null,
            after: ApprovalSnapshot.Of(request));

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await notifications.SendAsync(
            new Notification(
                NotificationChannels.InApp,
                request.RequiredPermission,
                $"Approval needed: {request.RequestType}",
                $"{request.RequestedByUserName} asked for {request.RequestType} on {request.SubjectType} {request.SubjectId}."),
            cancellationToken).ConfigureAwait(false);

        return request;
    }

    /// <inheritdoc />
    public Task<ApprovalRequest> ApproveAsync(Guid id, string? note = null, CancellationToken cancellationToken = default) =>
        DecideAsync(id, AuditActions.ApprovalApproved, (request, now) => request.Approve(currentUser, note, now), requiresPermission: true, cancellationToken);

    /// <inheritdoc />
    public Task<ApprovalRequest> RejectAsync(Guid id, string? note = null, CancellationToken cancellationToken = default) =>
        DecideAsync(id, AuditActions.ApprovalRejected, (request, now) => request.Reject(currentUser, note, now), requiresPermission: true, cancellationToken);

    /// <inheritdoc />
    public Task<ApprovalRequest> CancelAsync(Guid id, string? note = null, CancellationToken cancellationToken = default) =>
        DecideAsync(id, AuditActions.ApprovalCancelled, (request, now) => request.Cancel(currentUser, note, now), requiresPermission: false, cancellationToken);

    /// <inheritdoc />
    public Task<ApprovalRequest?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.ApprovalRequests.FirstOrDefaultAsync(request => request.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApprovalRequest>> ListAsync(
        ApprovalStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var query = database.ApprovalRequests.AsNoTracking();

        // Matched against a non-nullable local: the status column is stored by name, and EF cannot
        // translate a nullable-to-converted-value comparison.
        if (status is { } wanted)
        {
            query = query.Where(request => request.Status == wanted);
        }

        // Ordered by key, not by RequestedAt: ids are Guid v7, so the primary-key index already
        // orders chronologically on Postgres and on the fast tier's SQLite alike.
        return await query
            .OrderByDescending(request => request.Id)
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ApprovalRequest> DecideAsync(
        Guid id,
        string action,
        Action<ApprovalRequest, DateTimeOffset> decide,
        bool requiresPermission,
        CancellationToken cancellationToken)
    {
        var request = await database.ApprovalRequests
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ApprovalNotFoundException(id);

        // Checked here rather than at the endpoint: the endpoint only knows the caller may decide
        // *something*, this is what says they may decide *this*.
        if (requiresPermission && !currentUser.HasPermission(request.RequiredPermission))
        {
            throw new ApprovalPermissionException(
                $"Deciding a '{request.RequestType}' request requires the '{request.RequiredPermission}' permission.");
        }

        var before = ApprovalSnapshot.Of(request);

        decide(request, clock.GetUtcNow());

        audit.Record(action, AuditEntityTypes.ApprovalRequest, request.Id.ToString(), before, ApprovalSnapshot.Of(request));

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await notifications.SendAsync(
            new Notification(
                NotificationChannels.InApp,
                request.RequestedByUserId,
                $"Your {request.RequestType} request was {request.Status.ToString().ToLowerInvariant()}",
                $"{request.DecidedByUserName} {request.Status.ToString().ToLowerInvariant()} it. {request.DecisionNote}".TrimEnd()),
            cancellationToken).ConfigureAwait(false);

        return request;
    }
}

/// <summary>
/// The before/after shape an approval is audited as. A dedicated record rather than the entity, so
/// changing the entity later cannot silently change the meaning of historic audit entries.
/// </summary>
/// <param name="Id">Which request.</param>
/// <param name="RequestType">What kind of decision.</param>
/// <param name="SubjectType">Kind of entity decided about.</param>
/// <param name="SubjectId">Identifier of that entity.</param>
/// <param name="RequiredPermission">Permission a decider had to hold.</param>
/// <param name="Status">State at the time of the snapshot.</param>
/// <param name="DecidedByUserId">Who decided, if anyone had.</param>
/// <param name="DecisionNote">What they said.</param>
public sealed record ApprovalSnapshot(
    Guid Id,
    string RequestType,
    string SubjectType,
    string SubjectId,
    string RequiredPermission,
    ApprovalStatus Status,
    string? DecidedByUserId,
    string? DecisionNote)
{
    /// <summary>Takes a snapshot of <paramref name="request"/> as it stands.</summary>
    public static ApprovalSnapshot Of(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ApprovalSnapshot(
            request.Id,
            request.RequestType,
            request.SubjectType,
            request.SubjectId,
            request.RequiredPermission,
            request.Status,
            request.DecidedByUserId,
            request.DecisionNote);
    }
}
