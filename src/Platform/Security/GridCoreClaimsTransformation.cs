using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GridCore.Platform.Security;

/// <summary>
/// Normalises provider-shaped role claims into standard <see cref="ClaimTypes.Role"/> claims, so
/// everything downstream — authorization handlers, <c>/api/me</c>, module code — sees one shape no
/// matter which OIDC provider issued the token.
/// </summary>
public sealed class GridCoreClaimsTransformation(IOptions<GridCoreAuthenticationOptions> options) : IClaimsTransformation
{
    private readonly GridCoreAuthenticationOptions _options = options.Value;

    /// <summary>
    /// Marker claim recording that this principal has already been normalised. ASP.NET Core may run
    /// the transformation more than once per request; without the marker roles would be duplicated.
    /// </summary>
    public const string NormalisedClaimType = "gridcore:roles-normalised";

    /// <inheritdoc />
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated is not true || principal.HasClaim(NormalisedClaimType, bool.TrueString))
        {
            return Task.FromResult(principal);
        }

        var roles = RoleClaimReader.ReadRoles(principal, _options.RolesClaimPath);

        var identity = new ClaimsIdentity();
        identity.AddClaim(new Claim(NormalisedClaimType, bool.TrueString));

        foreach (var role in roles)
        {
            if (!principal.HasClaim(ClaimTypes.Role, role))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }
        }

        principal.AddIdentity(identity);

        return Task.FromResult(principal);
    }
}
