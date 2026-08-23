using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using GridCore.AppHost;

namespace GridCore.AppHost.UnitTests;

/// <summary>
/// What the AppHost tells the API about its identity provider, and what the realm export contains.
/// Model and file inspection only — no Keycloak is started, so this stays in the fast tier.
/// </summary>
public class IdentityCompositionTests
{
    private static IDistributedApplicationBuilder Builder(string environment) =>
        DistributedApplication.CreateBuilder(["--environment", environment]);

    /// <summary>
    /// Resolves a resource's environment variables the way `aspire publish` would. Publish mode
    /// keeps this in the fast tier: no endpoints are allocated and no container is started, so URLs
    /// resolve to their manifest placeholders.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> EnvironmentOf(
        DistributedApplication application,
        IResource resource)
    {
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Publish)
            {
                Services = application.Services,
            });

        var configuration = await ExecutionConfigurationBuilder
            .Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext);

        return configuration.EnvironmentVariables.ToDictionary(StringComparer.Ordinal);
    }

    /// <summary>Keycloak imports every realm file it finds in this directory at first startup.</summary>
    private const string RealmImportDestination = "/opt/keycloak/data/import";

    private static bool ImportsARealm(IResource identity) =>
        identity.Annotations
            .OfType<ContainerFileSystemCallbackAnnotation>()
            .Any(files => files.DestinationPath.Equals(RealmImportDestination, StringComparison.Ordinal));

    [Fact]
    public void The_realm_is_imported_in_development()
    {
        var builder = Builder("Development");

        var infrastructure = builder.AddGridCoreInfrastructure();

        Assert.True(ImportsARealm(infrastructure.Identity.Resource));
    }

    [Fact]
    public void The_realm_with_its_test_users_is_not_imported_outside_development()
    {
        var builder = Builder("Production");

        var infrastructure = builder.AddGridCoreInfrastructure();

        Assert.False(ImportsARealm(infrastructure.Identity.Resource));
    }

    [Fact]
    public async Task The_host_is_pointed_at_the_gridcore_realm_by_configuration_alone()
    {
        var builder = Builder("Development");
        var infrastructure = builder.AddGridCoreInfrastructure();

        var webHost = builder.AddGridCoreWebHost(infrastructure);

        using var application = builder.Build();
        var environment = await EnvironmentOf(application, webHost.Resource);
        Assert.EndsWith($"/realms/{InfrastructureComposition.IdentityRealmName}", environment["Authentication__Authority"], StringComparison.Ordinal);
        Assert.Equal(InfrastructureComposition.IdentityApiClientId, environment["Authentication__Audience"]);
        Assert.Equal("false", environment["Authentication__RequireHttpsMetadata"]);
    }
}
