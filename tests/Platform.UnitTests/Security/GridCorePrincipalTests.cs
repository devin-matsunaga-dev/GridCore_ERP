using System.Security.Claims;
using GridCore.Platform.Security;

namespace GridCore.Platform.UnitTests.Security;

public class GridCorePrincipalTests
{
    private static ClaimsPrincipal Caller(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "TestBearer", nameType: "preferred_username", roleType: ClaimTypes.Role));

    [Fact]
    public void Reads_identity_from_the_token()
    {
        var user = Caller(
            new Claim("sub", "3f1b8a7e-0e6f-4f0a-9a1b-2c3d4e5f6071"),
            new Claim("preferred_username", "billing"),
            new Claim("email", "billing@gridcore.test"));

        Assert.Equal("3f1b8a7e-0e6f-4f0a-9a1b-2c3d4e5f6071", user.UserId());
        Assert.Equal("billing", user.UserName());
        Assert.Equal("billing@gridcore.test", user.Email());
    }

    [Fact]
    public void An_anonymous_caller_has_no_identity_and_no_permissions()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Null(anonymous.UserId());
        Assert.Empty(anonymous.Roles());
        Assert.Empty(anonymous.Permissions());
    }

    [Fact]
    public void Roles_are_deduplicated_and_returned_in_presentation_order()
    {
        var user = Caller(
            new Claim(ClaimTypes.Role, GridCoreRoles.Manager),
            new Claim(ClaimTypes.Role, GridCoreRoles.Billing),
            new Claim(ClaimTypes.Role, GridCoreRoles.Billing));

        Assert.Equal([GridCoreRoles.Billing, GridCoreRoles.Manager], user.Roles());
    }

    [Fact]
    public void Roles_belonging_to_another_system_are_dropped()
    {
        var user = Caller(
            new Claim(ClaimTypes.Role, "default-roles-gridcore"),
            new Claim(ClaimTypes.Role, "offline_access"),
            new Claim(ClaimTypes.Role, GridCoreRoles.Technician));

        Assert.Equal([GridCoreRoles.Technician], user.Roles());
    }

    [Fact]
    public void Permissions_are_the_union_of_the_roles_and_are_sorted()
    {
        var user = Caller(
            new Claim(ClaimTypes.Role, GridCoreRoles.Technician),
            new Claim(ClaimTypes.Role, GridCoreRoles.Warehouse));

        var permissions = user.Permissions();

        Assert.Contains(Permissions.WorkOrders.Complete, permissions);
        Assert.Contains(Permissions.Inventory.Adjust, permissions);
        Assert.Equal(permissions.OrderBy(p => p, StringComparer.Ordinal), permissions);
        Assert.True(user.HasPermission(Permissions.Assets.Write));
        Assert.False(user.HasPermission(Permissions.Platform.Admin));
    }
}
