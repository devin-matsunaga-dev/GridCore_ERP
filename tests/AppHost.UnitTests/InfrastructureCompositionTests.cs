using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using GridCore.AppHost;

namespace GridCore.AppHost.UnitTests;

/// <summary>
/// Builds the application model in memory and asserts what the AppHost composes. No containers
/// are started — this is model inspection, so it belongs in the fast tier.
/// </summary>
public class InfrastructureCompositionTests
{
    [Fact]
    public void AddGridCoreInfrastructure_composes_every_backing_service()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        var infrastructure = builder.AddGridCoreInfrastructure();

        Assert.Equal(InfrastructureComposition.DatabaseResourceName, infrastructure.Database.Resource.Name);
        Assert.Equal(InfrastructureComposition.CacheResourceName, infrastructure.Cache.Resource.Name);
        Assert.Equal(InfrastructureComposition.BusResourceName, infrastructure.Bus.Resource.Name);
        Assert.Equal(InfrastructureComposition.IdentityResourceName, infrastructure.Identity.Resource.Name);
        Assert.Equal(InfrastructureComposition.ObjectStoreResourceName, infrastructure.ObjectStore.Resource.Name);

        var names = builder.Resources.Select(r => r.Name).ToList();
        Assert.Contains(InfrastructureComposition.PostgresResourceName, names);
        Assert.Contains(InfrastructureComposition.DatabaseResourceName, names);
        Assert.Contains(InfrastructureComposition.CacheResourceName, names);
        Assert.Contains(InfrastructureComposition.BusResourceName, names);
        Assert.Contains(InfrastructureComposition.IdentityResourceName, names);
        Assert.Contains(InfrastructureComposition.ObjectStoreResourceName, names);
    }

    [Theory]
    [InlineData(InfrastructureComposition.PostgresResourceName)]
    [InlineData(InfrastructureComposition.BusResourceName)]
    [InlineData(InfrastructureComposition.IdentityResourceName)]
    [InlineData(InfrastructureComposition.ObjectStoreResourceName)]
    public void AddGridCoreInfrastructure_gives_each_stateful_container_a_data_volume(string resourceName)
    {
        var builder = DistributedApplication.CreateBuilder([]);
        builder.AddGridCoreInfrastructure();

        var resource = Assert.Single(builder.Resources, r => r.Name == resourceName);

        Assert.Contains(
            resource.Annotations.OfType<ContainerMountAnnotation>(),
            mount => mount.Type == ContainerMountType.Volume);
    }

    [Fact]
    public void AddGridCoreInfrastructure_pins_the_minio_image_rather_than_tracking_latest()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        var infrastructure = builder.AddGridCoreInfrastructure();

        var image = Assert.Single(infrastructure.ObjectStore.Resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.NotNull(image.Tag);
        Assert.NotEqual("latest", image.Tag);
    }

    [Fact]
    public void AddGridCoreInfrastructure_rejects_a_null_builder()
    {
        IDistributedApplicationBuilder? builder = null;

        Assert.Throws<ArgumentNullException>(() => builder!.AddGridCoreInfrastructure());
    }
}
