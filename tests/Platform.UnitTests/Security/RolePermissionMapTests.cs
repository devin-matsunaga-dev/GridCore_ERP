using GridCore.Platform.Security;

namespace GridCore.Platform.UnitTests.Security;

public class RolePermissionMapTests
{
    [Theory]
    [InlineData(GridCoreRoles.Administrator)]
    [InlineData(GridCoreRoles.CustomerService)]
    [InlineData(GridCoreRoles.Billing)]
    [InlineData(GridCoreRoles.Finance)]
    [InlineData(GridCoreRoles.Warehouse)]
    [InlineData(GridCoreRoles.Technician)]
    [InlineData(GridCoreRoles.Supervisor)]
    [InlineData(GridCoreRoles.Manager)]
    public void Every_role_grants_something(string role)
    {
        Assert.NotEmpty(RolePermissionMap.PermissionsForRole(role));
    }

    [Fact]
    public void The_realm_defines_exactly_the_eight_roles()
    {
        Assert.Equal(8, GridCoreRoles.All.Count);
        Assert.Equal(GridCoreRoles.All.Count, GridCoreRoles.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Administrator_holds_every_declared_permission()
    {
        var granted = RolePermissionMap.PermissionsForRole(GridCoreRoles.Administrator);

        Assert.Equal(Permissions.All.OrderBy(p => p, StringComparer.Ordinal), granted.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_granted_permission_is_a_declared_one()
    {
        foreach (var role in GridCoreRoles.All)
        {
            Assert.All(RolePermissionMap.PermissionsForRole(role), permission => Assert.Contains(permission, Permissions.All));
        }
    }

    [Theory]
    [InlineData(GridCoreRoles.Billing, Permissions.Billing.Adjust)]
    [InlineData(GridCoreRoles.Manager, Permissions.Purchasing.Approve)]
    [InlineData(GridCoreRoles.Finance, Permissions.Finance.Post)]
    [InlineData(GridCoreRoles.Warehouse, Permissions.Inventory.Adjust)]
    [InlineData(GridCoreRoles.Supervisor, Permissions.WorkOrders.Assign)]
    [InlineData(GridCoreRoles.Technician, Permissions.WorkOrders.Complete)]
    [InlineData(GridCoreRoles.CustomerService, Permissions.Payments.Record)]
    public void A_role_holds_the_permission_its_job_needs(string role, string permission)
    {
        Assert.True(RolePermissionMap.HasPermission([role], permission));
    }

    [Theory]
    [InlineData(GridCoreRoles.Technician, Permissions.Billing.Adjust)]
    [InlineData(GridCoreRoles.CustomerService, Permissions.Finance.Post)]
    [InlineData(GridCoreRoles.Warehouse, Permissions.Customers.Write)]
    [InlineData(GridCoreRoles.Manager, Permissions.Platform.Admin)]
    [InlineData(GridCoreRoles.Supervisor, Permissions.Inventory.Adjust)]
    public void A_role_is_denied_a_permission_outside_its_job(string role, string permission)
    {
        Assert.False(RolePermissionMap.HasPermission([role], permission));
    }

    [Fact]
    public void Only_the_administrator_may_administer()
    {
        var holders = GridCoreRoles.All
            .Where(role => RolePermissionMap.HasPermission([role], Permissions.Platform.Admin))
            .ToList();

        Assert.Equal([GridCoreRoles.Administrator], holders);
    }

    [Fact]
    public void Several_roles_union_their_permissions()
    {
        var granted = RolePermissionMap.PermissionsForRoles([GridCoreRoles.Technician, GridCoreRoles.Warehouse]);

        Assert.Contains(Permissions.WorkOrders.Complete, granted);
        Assert.Contains(Permissions.Inventory.Adjust, granted);
        Assert.DoesNotContain(Permissions.Billing.Adjust, granted);
    }

    [Fact]
    public void An_unknown_role_grants_nothing()
    {
        Assert.Empty(RolePermissionMap.PermissionsForRole("SomeOtherSystemsRole"));
        Assert.False(RolePermissionMap.HasPermission(["SomeOtherSystemsRole"], Permissions.Customers.Read));
    }

    [Fact]
    public void No_roles_at_all_grants_nothing()
    {
        Assert.Empty(RolePermissionMap.PermissionsForRoles([]));
    }
}
