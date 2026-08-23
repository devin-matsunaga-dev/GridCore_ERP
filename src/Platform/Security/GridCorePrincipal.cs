using System.Security.Claims;

namespace GridCore.Platform.Security;

/// <summary>
/// Reads GridCore's view of a caller off a <see cref="ClaimsPrincipal"/>. The one place the rest of
/// the codebase asks "who is this and what may they do" — audit (WP-0.4) and every module use these
/// rather than digging through claims.
/// </summary>
public static class GridCorePrincipal
{
    /// <summary>The caller's stable identity-provider subject id, or <see langword="null"/> when anonymous.</summary>
    public static string? UserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
    }

    /// <summary>The caller's display name, falling back to the subject id.</summary>
    public static string? UserName(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.Identity?.Name
            ?? principal.FindFirstValue("preferred_username")
            ?? principal.UserId();
    }

    /// <summary>The caller's email address, when the token carries one.</summary>
    public static string? Email(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");
    }

    /// <summary>
    /// The caller's GridCore roles, normalised and ordered. Roles the map does not know are dropped:
    /// the identity provider may carry roles belonging to other systems.
    /// </summary>
    public static IReadOnlyList<string> Roles(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal
            .FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Where(GridCoreRoles.IsKnown)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(GridCoreRoles.OrderOf)
            .ToList();
    }

    /// <summary>Everything the caller's roles grant, ordered for display.</summary>
    public static IReadOnlyList<string> Permissions(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return RolePermissionMap
            .PermissionsForRoles(principal.Roles())
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Whether the caller holds <paramref name="permission"/>.</summary>
    public static bool HasPermission(this ClaimsPrincipal principal, string permission)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return RolePermissionMap.HasPermission(principal.Roles(), permission);
    }
}
