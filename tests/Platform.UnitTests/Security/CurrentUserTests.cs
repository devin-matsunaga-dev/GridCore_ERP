using System.Security.Claims;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Http;

namespace GridCore.Platform.UnitTests.Security;

public class CurrentUserTests
{
    private static IHttpContextAccessor Accessor(HttpContext? context) =>
        new HttpContextAccessor { HttpContext = context };

    private static DefaultHttpContext Request(params Claim[] claims) =>
        new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                claims,
                authenticationType: "TestBearer",
                nameType: "preferred_username",
                roleType: ClaimTypes.Role)),
        };

    [Fact]
    public void The_caller_of_a_request_is_read_off_their_token()
    {
        var user = new HttpContextCurrentUser(Accessor(Request(
            new Claim("sub", "user-7"),
            new Claim("preferred_username", "manager"),
            new Claim(ClaimTypes.Role, GridCoreRoles.Manager))));

        Assert.Equal("user-7", user.UserId);
        Assert.Equal("manager", user.UserName);
        Assert.True(user.HasPermission(Permissions.Platform.Approve));
        Assert.False(user.HasPermission(Permissions.Inventory.Adjust));
    }

    [Fact]
    public void Outside_a_request_the_actor_is_the_system_so_audit_is_never_written_against_nobody()
    {
        var user = new HttpContextCurrentUser(Accessor(context: null));

        Assert.Equal(SystemUser.SystemUserId, user.UserId);
        Assert.Equal(SystemUser.SystemUserId, user.UserName);
        Assert.True(user.HasPermission(Permissions.Finance.Post));
    }

    [Fact]
    public void An_anonymous_caller_inside_a_request_is_not_the_system_and_holds_nothing()
    {
        var user = new HttpContextCurrentUser(Accessor(new DefaultHttpContext()));

        Assert.False(user.HasPermission(Permissions.Platform.Approve));
        Assert.False(user.HasPermission(Permissions.Customers.Read));
    }

    [Fact]
    public void A_token_with_no_subject_claim_falls_back_to_the_system_id_rather_than_null()
    {
        var user = new HttpContextCurrentUser(Accessor(Request(new Claim(ClaimTypes.Role, GridCoreRoles.Billing))));

        Assert.Equal(SystemUser.SystemUserId, user.UserId);
    }
}
