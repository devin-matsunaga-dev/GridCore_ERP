using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GridCore.Platform.Security;

/// <summary>Host-side wiring for authentication and permission-based authorization.</summary>
public static class SecurityRegistration
{
    /// <summary>
    /// Registers OIDC bearer authentication and permission-based authorization from the
    /// <c>Authentication</c> configuration section. Deliberately plain <see cref="JwtBearerDefaults"/>
    /// rather than a Keycloak-specific integration: swapping identity provider is then a change of
    /// <c>Authentication:Authority</c> and <c>Authentication:RolesClaimPath</c>, nothing more.
    /// </summary>
    /// <exception cref="InvalidOperationException">The <c>Authentication</c> section is missing or incomplete.</exception>
    public static IServiceCollection AddGridCoreSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(GridCoreAuthenticationOptions.SectionName);
        var options = section.Get<GridCoreAuthenticationOptions>() ?? new GridCoreAuthenticationOptions();

        // Fail fast and loudly: a host that cannot validate tokens would otherwise 401 every
        // request and look like a credentials problem.
        options.Validate();

        services.Configure<GridCoreAuthenticationOptions>(section);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.Audience = options.Audience;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.NameClaimType = options.NameClaimType;

                // Roles arrive in a provider-specific shape and are normalised to ClaimTypes.Role
                // by GridCoreClaimsTransformation, which is what the handlers read.
                jwt.TokenValidationParameters.RoleClaimType = System.Security.Claims.ClaimTypes.Role;
                jwt.TokenValidationParameters.ValidateIssuer = true;
                jwt.TokenValidationParameters.ValidateAudience = true;
                jwt.TokenValidationParameters.ValidateLifetime = true;
            });

        services.AddSingleton<IClaimsTransformation, GridCoreClaimsTransformation>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, PermissionAuthorizationHandler>());
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();

        services.AddAuthorizationBuilder()
            // Secure by default: an endpoint is authenticated unless it opts out with
            // AllowAnonymous, so a new module endpoint cannot ship accidentally public.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }

    /// <summary>Requires <paramref name="permission"/> — see <see cref="Permissions"/> — to call this endpoint.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.RequireAuthorization(PermissionPolicy.NameFor(permission));
    }
}
