using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using GridCore.AppHost;

namespace GridCore.AppHost.UnitTests;

public class WebCompositionTests
{
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
        var webHost = builder.AddGridCoreWebHost(builder.AddGridCoreInfrastructure());

        var webApp = builder.AddGridCoreWebApp(webHost, directoryExists: _ => true);

        Assert.NotNull(webApp);
        Assert.Equal(WebComposition.WebAppResourceName, webApp.Resource.Name);
        Assert.Contains(builder.Resources, r => r.Name == WebComposition.WebAppResourceName);
    }

    /// <summary>Failure path: `web/` does not exist until WP-0.6, and `aspire run` must still come up.</summary>
    [Fact]
    public void AddGridCoreWebApp_skips_the_dev_server_when_web_has_not_been_created()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var webHost = builder.AddGridCoreWebHost(builder.AddGridCoreInfrastructure());

        var webApp = builder.AddGridCoreWebApp(webHost, directoryExists: _ => false);

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
}
