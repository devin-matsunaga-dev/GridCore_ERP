using System.Security.Claims;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Options;

namespace GridCore.Platform.UnitTests.Security;

public class PermissionAuthorizationHandlerTests
{
    private static ClaimsPrincipal Caller(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(role => new Claim(ClaimTypes.Role, role)), authenticationType: "TestBearer"));

    private static async Task<bool> Evaluate(ClaimsPrincipal user, string permission)
    {
        var requirement = new PermissionRequirement(permission);
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);

        await new PermissionAuthorizationHandler().HandleAsync(context);

        return context.HasSucceeded;
    }

    [Fact]
    public async Task A_role_that_grants_the_permission_is_allowed()
    {
        Assert.True(await Evaluate(Caller(GridCoreRoles.Billing), Permissions.Billing.Adjust));
    }

    [Fact]
    public async Task A_role_that_does_not_grant_the_permission_is_denied()
    {
        Assert.False(await Evaluate(Caller(GridCoreRoles.Technician), Permissions.Billing.Adjust));
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_denied_even_holding_the_role_claim()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, GridCoreRoles.Administrator)]));

        Assert.False(await Evaluate(anonymous, Permissions.Platform.Admin));
    }

    [Fact]
    public async Task A_role_the_realm_does_not_define_is_denied()
    {
        Assert.False(await Evaluate(Caller("Administrators"), Permissions.Platform.Admin));
    }

    [Fact]
    public async Task One_qualifying_role_out_of_several_is_enough()
    {
        Assert.True(await Evaluate(Caller(GridCoreRoles.Technician, GridCoreRoles.Finance), Permissions.Finance.Post));
    }
}

public class PermissionPolicyProviderTests
{
    private static PermissionPolicyProvider Provider(Action<AuthorizationOptions>? configure = null)
    {
        var options = new AuthorizationOptions();
        configure?.Invoke(options);

        return new PermissionPolicyProvider(Options.Create(options));
    }

    [Fact]
    public async Task Builds_a_policy_for_any_permission_name()
    {
        var policy = await Provider().GetPolicyAsync(PermissionPolicy.NameFor(Permissions.Inventory.Adjust));

        Assert.NotNull(policy);
        var requirement = Assert.Single(policy.Requirements.OfType<PermissionRequirement>());
        Assert.Equal(Permissions.Inventory.Adjust, requirement.Permission);
    }

    [Fact]
    public async Task The_built_policy_also_requires_an_authenticated_caller()
    {
        var policy = await Provider().GetPolicyAsync(PermissionPolicy.NameFor(Permissions.Finance.Post));

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task Falls_back_to_the_default_provider_for_an_ordinary_policy_name()
    {
        var provider = Provider(options => options.AddPolicy("named", builder => builder.RequireAssertion(_ => true)));

        Assert.NotNull(await provider.GetPolicyAsync("named"));
        Assert.Null(await provider.GetPolicyAsync("no-such-policy"));
    }

    [Fact]
    public async Task A_prefix_with_no_permission_after_it_is_not_a_permission_policy()
    {
        Assert.Null(PermissionPolicy.PermissionFor(PermissionPolicy.Prefix));
        Assert.Null(await Provider().GetPolicyAsync(PermissionPolicy.Prefix));
    }

    [Fact]
    public void Policy_names_round_trip()
    {
        Assert.Equal(Permissions.Billing.Adjust, PermissionPolicy.PermissionFor(PermissionPolicy.NameFor(Permissions.Billing.Adjust)));
    }
}
