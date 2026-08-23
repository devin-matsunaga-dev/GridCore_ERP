using System.Security.Claims;
using GridCore.Platform.Security;

namespace GridCore.Platform.UnitTests.Security;

public class RoleClaimReaderTests
{
    private const string KeycloakPath = GridCoreAuthenticationOptions.KeycloakRealmRolesClaimPath;

    private static ClaimsPrincipal PrincipalWith(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "TestBearer"));

    [Fact]
    public void Reads_keycloak_realm_roles_from_the_nested_json_claim()
    {
        var principal = PrincipalWith(new Claim("realm_access", """{"roles":["Billing","Manager"]}"""));

        var roles = RoleClaimReader.ReadRoles(principal, KeycloakPath);

        Assert.Equal(["Billing", "Manager"], roles);
    }

    [Fact]
    public void Reads_flat_role_claims_when_the_path_has_one_segment()
    {
        var principal = PrincipalWith(new Claim("roles", "Finance"), new Claim("roles", "Manager"));

        var roles = RoleClaimReader.ReadRoles(principal, "roles");

        Assert.Equal(["Finance", "Manager"], roles);
    }

    [Fact]
    public void Reads_a_json_array_claim_when_the_path_has_one_segment()
    {
        var principal = PrincipalWith(new Claim("roles", """["Finance","Manager"]"""));

        var roles = RoleClaimReader.ReadRoles(principal, "roles");

        Assert.Equal(["Finance", "Manager"], roles);
    }

    [Fact]
    public void Merges_roles_across_repeated_claims_and_drops_duplicates()
    {
        var principal = PrincipalWith(
            new Claim("realm_access", """{"roles":["Billing"]}"""),
            new Claim("realm_access", """{"roles":["Billing","Finance"]}"""));

        var roles = RoleClaimReader.ReadRoles(principal, KeycloakPath);

        Assert.Equal(["Billing", "Finance"], roles);
    }

    [Fact]
    public void Ignores_non_string_entries_in_the_roles_array()
    {
        var principal = PrincipalWith(new Claim("realm_access", """{"roles":["Billing",42,null,{"nested":true}]}"""));

        var roles = RoleClaimReader.ReadRoles(principal, KeycloakPath);

        Assert.Equal(["Billing"], roles);
    }

    [Fact]
    public void A_malformed_json_claim_yields_no_roles_rather_than_throwing()
    {
        var principal = PrincipalWith(new Claim("realm_access", "not json at all"));

        Assert.Empty(RoleClaimReader.ReadRoles(principal, KeycloakPath));
    }

    [Fact]
    public void A_claim_missing_the_configured_path_yields_no_roles()
    {
        var principal = PrincipalWith(new Claim("realm_access", """{"groups":["Billing"]}"""));

        Assert.Empty(RoleClaimReader.ReadRoles(principal, KeycloakPath));
    }

    [Fact]
    public void A_principal_with_no_such_claim_yields_no_roles()
    {
        Assert.Empty(RoleClaimReader.ReadRoles(PrincipalWith(new Claim("sub", "abc")), KeycloakPath));
    }

    [Fact]
    public void A_blank_path_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => RoleClaimReader.ReadRoles(PrincipalWith(), "  "));
    }
}
