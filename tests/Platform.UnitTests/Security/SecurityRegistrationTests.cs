using System.Security.Claims;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Platform.UnitTests.Security;

public class SecurityRegistrationTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Authentication:Authority"] = "http://localhost:8080/realms/gridcore",
            ["Authentication:Audience"] = "gridcore-api",
            ["Authentication:RequireHttpsMetadata"] = "false",
        };

        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static ServiceProvider Configured(params (string Key, string? Value)[] overrides)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGridCoreSecurity(Configuration(overrides));

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Registers_bearer_authentication_as_the_default_scheme()
    {
        await using var provider = Configured();

        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(await schemes.GetSchemeAsync(JwtBearerDefaults.AuthenticationScheme));
    }

    [Fact]
    public void Registers_the_permission_policy_provider_and_handler()
    {
        using var provider = Configured();

        Assert.IsType<PermissionPolicyProvider>(provider.GetRequiredService<IAuthorizationPolicyProvider>());
        Assert.Contains(provider.GetServices<IAuthorizationHandler>(), handler => handler is PermissionAuthorizationHandler);
        Assert.Contains(provider.GetServices<IClaimsTransformation>(), t => t is GridCoreClaimsTransformation);
    }

    [Fact]
    public async Task Endpoints_are_authenticated_by_default()
    {
        await using var provider = Configured();

        var fallback = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();

        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task The_configured_roles_claim_path_reaches_the_claims_transformation()
    {
        await using var provider = Configured(("Authentication:RolesClaimPath", "roles"));

        var transformation = provider.GetServices<IClaimsTransformation>().OfType<GridCoreClaimsTransformation>().Single();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("roles", GridCoreRoles.Manager)], "TestBearer"));

        var transformed = await transformation.TransformAsync(principal);

        Assert.Equal([GridCoreRoles.Manager], transformed.Roles());
    }

    /// <summary>
    /// The decision the routing layer makes, taken through the real container: policy provider,
    /// requirement and handler as the host composes them. An unmet requirement on an authenticated
    /// caller is what ASP.NET Core turns into a 403.
    /// </summary>
    [Theory]
    [InlineData(GridCoreRoles.Administrator, Permissions.Platform.Admin, true)]
    [InlineData(GridCoreRoles.Billing, Permissions.Billing.Adjust, true)]
    [InlineData(GridCoreRoles.Technician, Permissions.Billing.Adjust, false)]
    [InlineData(GridCoreRoles.Manager, Permissions.Platform.Admin, false)]
    public async Task The_composed_pipeline_admits_only_a_caller_whose_role_grants_the_permission(
        string role, string permission, bool expectedAllowed)
    {
        await using var provider = Configured();
        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "TestBearer"));

        var result = await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(caller, resource: null, PermissionPolicy.NameFor(permission));

        Assert.Equal(expectedAllowed, result.Succeeded);
    }

    [Fact]
    public async Task The_composed_pipeline_turns_a_keycloak_token_into_permissions()
    {
        await using var provider = Configured();
        var token = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("realm_access", """{"roles":["Warehouse","offline_access"]}""")],
            "TestBearer"));

        var caller = await provider.GetServices<IClaimsTransformation>()
            .OfType<GridCoreClaimsTransformation>()
            .Single()
            .TransformAsync(token);

        var authorization = provider.GetRequiredService<IAuthorizationService>();

        Assert.True((await authorization.AuthorizeAsync(caller, null, PermissionPolicy.NameFor(Permissions.Inventory.Adjust))).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(caller, null, PermissionPolicy.NameFor(Permissions.Finance.Post))).Succeeded);
    }

    [Fact]
    public async Task An_anonymous_caller_never_satisfies_a_permission_policy()
    {
        await using var provider = Configured();

        var result = await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, PermissionPolicy.NameFor(Permissions.Customers.Read));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void A_host_with_no_authority_configured_refuses_to_start()
    {
        var services = new ServiceCollection();

        var act = () => services.AddGridCoreSecurity(Configuration(("Authentication:Authority", null)));

        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("Authentication:Authority", ex.Message, StringComparison.Ordinal);
    }
}
