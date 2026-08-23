using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Platform.Security;

/// <summary>The signed-in caller, as GridCore sees them.</summary>
/// <param name="UserId">Identity-provider subject id; the value audit entries are written against.</param>
/// <param name="UserName">Display name.</param>
/// <param name="Email">Email address, when the token carries one.</param>
/// <param name="Roles">GridCore roles held, in presentation order.</param>
/// <param name="Permissions">Everything those roles grant — what the SPA hides or shows on.</param>
public sealed record MeResponse(
    string UserId,
    string? UserName,
    string? Email,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);

/// <summary>Result of the permission-gated probe endpoint.</summary>
/// <param name="Permission">The permission the caller had to hold to get this response.</param>
/// <param name="UserName">Who got through.</param>
public sealed record PermissionProbeResponse(string Permission, string? UserName);

/// <summary>Platform-owned identity endpoints. Mapped by the host alongside the modules.</summary>
public static class MeEndpoints
{
    /// <summary>Route of the current-user endpoint.</summary>
    public const string MeRoute = "/api/me";

    /// <summary>
    /// Route of the permission probe. Exists so RBAC can be checked end to end without a business
    /// feature: any role reaches <see cref="MeRoute"/>, only an administrator reaches this.
    /// </summary>
    public const string PermissionProbeRoute = "/api/me/admin-probe";

    /// <summary>Maps <c>/api/me</c> and the permission probe.</summary>
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet(MeRoute, (HttpContext context) =>
            {
                var user = context.User;
                var userId = user.UserId();

                // The fallback policy guarantees an authenticated caller; a token with no subject
                // claim is unusable for audit, so it is rejected rather than silently attributed.
                return userId is null
                    ? Results.Problem(
                        title: "Unusable token",
                        detail: "The access token carries no subject claim, so the caller cannot be identified.",
                        statusCode: StatusCodes.Status401Unauthorized)
                    : Results.Ok(new MeResponse(userId, user.UserName(), user.Email(), user.Roles(), user.Permissions()));
            })
            .WithName("GetCurrentUser")
            .WithTags("Identity");

        endpoints
            .MapGet(PermissionProbeRoute, (HttpContext context) =>
                Results.Ok(new PermissionProbeResponse(Permissions.Platform.Admin, context.User.UserName())))
            .RequirePermission(Permissions.Platform.Admin)
            .WithName("ProbeAdminPermission")
            .WithTags("Identity");

        return endpoints;
    }
}
