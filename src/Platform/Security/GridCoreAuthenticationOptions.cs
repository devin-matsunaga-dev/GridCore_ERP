namespace GridCore.Platform.Security;

/// <summary>
/// Everything GridCore needs to know about its OIDC provider, bound from the
/// <c>Authentication</c> configuration section. Keycloak is what the AppHost runs, but nothing
/// here is Keycloak-specific: pointing <see cref="Authority"/> at another OIDC provider and
/// adjusting <see cref="RolesClaimPath"/> is the whole swap — no domain or endpoint code changes.
/// </summary>
public sealed class GridCoreAuthenticationOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Authentication";

    /// <summary>
    /// Path of the claim carrying the caller's roles, as it appears in a Keycloak access token.
    /// </summary>
    public const string KeycloakRealmRolesClaimPath = "realm_access.roles";

    /// <summary>
    /// OIDC issuer, e.g. <c>http://localhost:8080/realms/gridcore</c>. Discovery metadata is read
    /// from <c>{Authority}/.well-known/openid-configuration</c>. Supplied by the AppHost in
    /// development; by the environment in production.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Audience every access token must carry — the API's client id.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Whether the metadata endpoint must be HTTPS. False only for local development, where the
    /// AppHost runs Keycloak over plain HTTP on the container network.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Dotted path to the roles in the access token. A single segment (<c>roles</c>) reads role
    /// claims directly; a nested path (<c>realm_access.roles</c>) reads the first segment as a JSON
    /// object claim and walks into it. Defaults to Keycloak's realm-role shape.
    /// </summary>
    public string RolesClaimPath { get; set; } = KeycloakRealmRolesClaimPath;

    /// <summary>Claim carrying the caller's display name.</summary>
    public string NameClaimType { get; set; } = "preferred_username";

    /// <summary>
    /// Throws when the options cannot produce a working authentication scheme. Called at startup so
    /// a misconfigured host fails immediately and loudly rather than 401-ing every request.
    /// </summary>
    /// <exception cref="InvalidOperationException">A required value is missing or malformed.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Authority))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Authority is required — set it to the OIDC issuer URL (the AppHost supplies it in development).");
        }

        // Scheme-checked, not merely absolute: on Unix a bare path such as "/realms/gridcore"
        // parses as an absolute file URI and would otherwise slip through.
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority)
            || (authority.Scheme != Uri.UriSchemeHttps && authority.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Authority must be an absolute http(s) URL, but was '{Authority}'.");
        }

        if (string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Audience is required — set it to the API client id tokens are issued for.");
        }

        if (string.IsNullOrWhiteSpace(RolesClaimPath))
        {
            throw new InvalidOperationException(
                $"{SectionName}:RolesClaimPath is required — e.g. '{KeycloakRealmRolesClaimPath}' for Keycloak realm roles.");
        }
    }
}
