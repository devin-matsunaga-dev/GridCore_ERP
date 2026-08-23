using System.Security.Claims;
using GridCore.Platform.Security;
using Microsoft.Extensions.Options;

namespace GridCore.Platform.UnitTests.Security;

public class GridCoreClaimsTransformationTests
{
    private static GridCoreClaimsTransformation Transformation(string? rolesClaimPath = null) =>
        new(Options.Create(new GridCoreAuthenticationOptions
        {
            Authority = "https://identity.example/realms/gridcore",
            Audience = "gridcore-api",
            RolesClaimPath = rolesClaimPath ?? GridCoreAuthenticationOptions.KeycloakRealmRolesClaimPath,
        }));

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "TestBearer"));

    [Fact]
    public async Task Normalises_keycloak_realm_roles_into_role_claims()
    {
        var principal = Authenticated(new Claim("realm_access", """{"roles":["Billing","Manager"]}"""));

        var transformed = await Transformation().TransformAsync(principal);

        Assert.Equal(["Billing", "Manager"], transformed.FindAll(ClaimTypes.Role).Select(c => c.Value));
    }

    [Fact]
    public async Task Running_twice_does_not_duplicate_roles()
    {
        var transformation = Transformation();
        var principal = Authenticated(new Claim("realm_access", """{"roles":["Billing"]}"""));

        var once = await transformation.TransformAsync(principal);
        var twice = await transformation.TransformAsync(once);

        Assert.Single(twice.FindAll(ClaimTypes.Role));
    }

    [Fact]
    public async Task An_anonymous_principal_is_left_alone()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var transformed = await Transformation().TransformAsync(principal);

        Assert.Empty(transformed.FindAll(ClaimTypes.Role));
        Assert.Empty(transformed.FindAll(GridCoreClaimsTransformation.NormalisedClaimType));
    }

    [Fact]
    public async Task A_token_with_unreadable_roles_gets_no_roles()
    {
        var principal = Authenticated(new Claim("realm_access", "}{"));

        var transformed = await Transformation().TransformAsync(principal);

        Assert.Empty(transformed.FindAll(ClaimTypes.Role));
        Assert.False(transformed.HasPermission(Permissions.Customers.Read));
    }

    [Fact]
    public async Task A_provider_using_flat_role_claims_needs_only_the_configured_path()
    {
        var principal = Authenticated(new Claim("roles", "Finance"));

        var transformed = await Transformation(rolesClaimPath: "roles").TransformAsync(principal);

        Assert.Equal([GridCoreRoles.Finance], transformed.Roles());
    }
}
