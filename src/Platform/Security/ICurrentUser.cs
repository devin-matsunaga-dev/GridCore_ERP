using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace GridCore.Platform.Security;

/// <summary>
/// Who is acting, for code that must not depend on <see cref="HttpContext"/>. Audit entries are
/// attributed to <see cref="UserId"/>, and services enforce permissions through
/// <see cref="HasPermission"/> rather than trusting the endpoint that called them.
/// </summary>
public interface ICurrentUser
{
    /// <summary>The identity-provider subject id of the caller, or <see cref="SystemUser.UserId"/> for background work.</summary>
    string UserId { get; }

    /// <summary>Display name of the caller, when one is known.</summary>
    string? UserName { get; }

    /// <summary>Whether the caller holds <paramref name="permission"/> — see <see cref="Permissions"/>.</summary>
    bool HasPermission(string permission);
}

/// <summary>
/// The host itself. Scheduled jobs and event consumers act as the system: audit is attributed to it
/// rather than to nobody, and its work is not user-gated — the permission checks exist to stop one
/// signed-in user doing another's job, not to constrain the host's own background work.
/// </summary>
public sealed class SystemUser : ICurrentUser
{
    /// <summary>The id audit entries are attributed to when there is no HTTP caller.</summary>
    public const string SystemUserId = "system";

    /// <summary>The shared instance.</summary>
    public static SystemUser Instance { get; } = new();

    /// <inheritdoc />
    public string UserId => SystemUserId;

    /// <inheritdoc />
    public string? UserName => SystemUserId;

    /// <inheritdoc />
    public bool HasPermission(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        return true;
    }
}

/// <summary>
/// The caller of the current request, read off the <see cref="ClaimsPrincipal"/> through
/// <see cref="GridCorePrincipal"/>. Outside a request — a scheduled job, an event consumer — it
/// defers to <see cref="SystemUser"/>. An anonymous caller inside a request is not the system: it
/// holds no permissions at all.
/// </summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    /// <inheritdoc />
    public string UserId => Principal?.UserId() ?? SystemUser.SystemUserId;

    /// <inheritdoc />
    public string? UserName => Principal?.UserName() ?? SystemUser.SystemUserId;

    /// <inheritdoc />
    public bool HasPermission(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        var principal = Principal;

        return principal is null
            ? SystemUser.Instance.HasPermission(permission)
            : principal.HasPermission(permission);
    }

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;
}
