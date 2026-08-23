using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace GridCore.Platform.Security;

/// <summary>Naming of the dynamically-built permission policies.</summary>
public static class PermissionPolicy
{
    /// <summary>Prefix that marks a policy name as a permission requirement.</summary>
    public const string Prefix = "perm:";

    /// <summary>The policy name that gates an endpoint on <paramref name="permission"/>.</summary>
    public static string NameFor(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        return Prefix + permission;
    }

    /// <summary>
    /// The permission a policy name gates on, or <see langword="null"/> when the name is not a
    /// permission policy (an ordinary named policy, which the default provider handles).
    /// </summary>
    public static string? PermissionFor(string policyName)
    {
        ArgumentNullException.ThrowIfNull(policyName);

        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var permission = policyName[Prefix.Length..];

        return string.IsNullOrWhiteSpace(permission) ? null : permission;
    }
}

/// <summary>Requires the caller to hold <paramref name="Permission"/> through one of their roles.</summary>
/// <param name="Permission">The permission being demanded, e.g. <c>billing.adjust</c>.</param>
public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>
/// Grants a <see cref="PermissionRequirement"/> when any role on the principal maps to the demanded
/// permission. Roles come from the token; the role-to-permission map is the only policy source, so
/// permissions are never carried in the token itself.
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.Identity?.IsAuthenticated is true)
        {
            var roles = context.User.FindAll(ClaimTypes.Role).Select(claim => claim.Value);

            if (RolePermissionMap.HasPermission(roles, requirement.Permission))
            {
                context.Succeed(requirement);
            }
        }

        // Not calling Fail() leaves the requirement unmet, which is a 403 for an authenticated
        // caller and a 401 otherwise — and lets another handler grant it in a later WP.
        return Task.CompletedTask;
    }
}
