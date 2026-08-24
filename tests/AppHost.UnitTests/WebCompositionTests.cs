using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using GridCore.AppHost;

namespace GridCore.AppHost.UnitTests;

public class WebCompositionTests
{
    /// <summary>
    /// Resolves a resource's environment variables the way `aspire publish` would, which keeps this
    /// in the fast tier: no endpoints are allocated and no container is started.
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

    [Fact]
    public void AddGridCoreWebHost_waits_for_every_backing_service()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var infrastructure = builder.AddGridCoreInfrastructure();

        var webHost = builder.AddGridCoreWebHost(infrastructure);

        var waitedFor = webHost.Resource.Annotations
            .OfType<WaitAnnotation>()
            .Select(w => w.Resource.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Waiting on the database also waits on the Postgres server that hosts it.
        string[] expected =
        [
            InfrastructureComposition.PostgresResourceName,
            InfrastructureComposition.BusResourceName,
            InfrastructureComposition.DatabaseResourceName,
            InfrastructureComposition.IdentityResourceName,
            InfrastructureComposition.ObjectStoreResourceName,
            InfrastructureComposition.CacheResourceName,
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), waitedFor);
    }

    [Fact]
    public void AddGridCoreWebHost_is_health_checked_on_the_aggregate_endpoint()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var infrastructure = builder.AddGridCoreInfrastructure();

        var webHost = builder.AddGridCoreWebHost(infrastructure);

        Assert.Equal(WebComposition.WebHostResourceName, webHost.Resource.Name);
        Assert.NotEmpty(webHost.Resource.Annotations.OfType<HealthCheckAnnotation>());
    }

    [Fact]
    public void AddGridCoreWebApp_adds_the_dev_server_once_web_exists()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var infrastructure = builder.AddGridCoreInfrastructure();
        var webHost = builder.AddGridCoreWebHost(infrastructure);

        var webApp = builder.AddGridCoreWebApp(webHost, infrastructure, directoryExists: _ => true);

        Assert.NotNull(webApp);
        Assert.Equal(WebComposition.WebAppResourceName, webApp.Resource.Name);
        Assert.Contains(builder.Resources, r => r.Name == WebComposition.WebAppResourceName);
    }

    /// <summary>Failure path: `web/` does not exist until WP-0.6, and `aspire run` must still come up.</summary>
    [Fact]
    public void AddGridCoreWebApp_skips_the_dev_server_when_web_has_not_been_created()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var infrastructure = builder.AddGridCoreInfrastructure();
        var webHost = builder.AddGridCoreWebHost(infrastructure);

        var webApp = builder.AddGridCoreWebApp(webHost, infrastructure, directoryExists: _ => false);

        Assert.Null(webApp);
        Assert.DoesNotContain(builder.Resources, r => r.Name == WebComposition.WebAppResourceName);
    }

    [Fact]
    public void TryLocateWebApp_resolves_web_at_the_repository_root()
    {
        var located = WebComposition.TryLocateWebApp(
            Path.Combine(Path.DirectorySeparatorChar.ToString(), "repo", "src", "AppHost"),
            _ => true,
            out var appDirectory);

        Assert.True(located);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "repo", "web")),
            appDirectory);
    }

    /// <summary>Failure path: a missing directory is reported, not thrown.</summary>
    [Fact]
    public void TryLocateWebApp_reports_a_missing_app_without_throwing()
    {
        var located = WebComposition.TryLocateWebApp(
            Path.Combine(Path.DirectorySeparatorChar.ToString(), "repo", "src", "AppHost"),
            _ => false,
            out var appDirectory);

        Assert.False(located);
        Assert.EndsWith(WebComposition.WebAppDirectoryName, appDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void AddGridCoreWebHost_rejects_missing_infrastructure()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        Assert.Throws<ArgumentNullException>(() => builder.AddGridCoreWebHost(null!));
    }

    [Fact]
    public async Task The_spa_logs_in_against_the_same_realm_the_api_validates_against()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var infrastructure = builder.AddGridCoreInfrastructure();
        var webHost = builder.AddGridCoreWebHost(infrastructure);

        var webApp = builder.AddGridCoreWebApp(webHost, infrastructure, directoryExists: _ => true);

        using var application = builder.Build();
        var spa = await EnvironmentOf(application, webApp!.Resource);
        var api = await EnvironmentOf(application, webHost.Resource);

        // Same issuer on both sides, or the API rejects every token the SPA obtains.
        Assert.Equal(api["Authentication__Authority"], spa["VITE_OIDC_AUTHORITY"]);
        Assert.Equal(InfrastructureComposition.IdentityWebClientId, spa["VITE_OIDC_CLIENT_ID"]);
        Assert.Equal(api["Authentication__Audience"], spa["VITE_OIDC_AUDIENCE"]);
    }

    /// <summary>Vite hides anything without the prefix from the browser bundle.</summary>
    [Fact]
    public async Task Every_setting_handed_to_the_spa_is_vite_prefixed()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var infrastructure = builder.AddGridCoreInfrastructure();
        var webHost = builder.AddGridCoreWebHost(infrastructure);

        var webApp = builder.AddGridCoreWebApp(webHost, infrastructure, directoryExists: _ => true);

        using var application = builder.Build();
        var spa = await EnvironmentOf(application, webApp!.Resource);

        var oidcSettings = spa.Keys.Where(key => key.Contains("OIDC", StringComparison.Ordinal));
        Assert.NotEmpty(oidcSettings);
        Assert.All(oidcSettings, key => Assert.StartsWith("VITE_", key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Regression: with Aspire's default proxied endpoint the browser is handed a random port, and
    /// Keycloak answers the login with "Invalid parameter: redirect_uri".
    /// </summary>
    [Fact]
    public void The_dev_server_is_served_unproxied_on_the_port_the_realm_registers()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var infrastructure = builder.AddGridCoreInfrastructure();
        var webHost = builder.AddGridCoreWebHost(infrastructure);

        var webApp = builder.AddGridCoreWebApp(webHost, infrastructure, directoryExists: _ => true);

        var endpoint = Assert.Single(
            webApp!.Resource.Annotations.OfType<EndpointAnnotation>(),
            e => e.Name == WebComposition.WebAppEndpointName);

        Assert.Equal(WebComposition.WebAppPort, endpoint.Port);
        Assert.Equal(WebComposition.WebAppPort, endpoint.TargetPort);
        Assert.False(endpoint.IsProxied);
    }
}
