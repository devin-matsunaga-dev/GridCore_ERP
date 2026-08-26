using GridCore.Contracts.Providers;
using GridCore.Platform.Documents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GridCore.Platform.UnitTests.Documents;

/// <summary>
/// The object store's <b>configuration</b>, which is all of it that can be tested without a
/// container (CONVENTIONS.md rule C). The round trip against real MinIO is one gate-tier test; what
/// belongs here is that a host misconfigured for it says so in a sentence rather than failing at
/// the counter with a null reference.
/// </summary>
public class MinioDocumentStoreTests
{
    private static MinioDocumentStoreOptions Options(
        string? endpoint = "http://localhost:9000",
        string? accessKey = "gridcore",
        string? secretKey = "gridcore-secret") =>
        new() { Endpoint = endpoint, AccessKey = accessKey, SecretKey = secretKey };

    private static IConfiguration Configuration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

    [Fact]
    public void A_configured_store_builds()
    {
        using var store = new MinioDocumentStore(Options());

        Assert.NotNull(store);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_endpoint_is_refused_by_name(string? endpoint)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => new MinioDocumentStore(Options(endpoint: endpoint)));

        Assert.Contains($"{MinioDocumentStoreOptions.SectionName}:Endpoint", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("localhost:9000")]
    [InlineData("/minio")]
    [InlineData("ftp://localhost:9000")]
    public void An_endpoint_that_is_not_an_http_url_is_refused_by_name(string endpoint)
    {
        // "localhost:9000" is the shape somebody types from memory, and Uri parses it as an absolute
        // URI whose scheme is "localhost" — so the parse alone is not the check. Without the scheme
        // test the SDK rejects it several layers down as "Endpoint not initialized", which names
        // nothing an operator can act on.
        var refused = Assert.Throws<InvalidOperationException>(() => new MinioDocumentStore(Options(endpoint: endpoint)));

        Assert.Contains("http://", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "secret")]
    [InlineData("access", null)]
    public void Missing_credentials_are_refused_by_name(string? accessKey, string? secretKey)
    {
        var refused = Assert.Throws<InvalidOperationException>(
            () => new MinioDocumentStore(Options(accessKey: accessKey, secretKey: secretKey)));

        Assert.Contains("AccessKey", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bucket_has_a_default_so_a_fresh_volume_needs_no_provisioning() =>
        Assert.False(string.IsNullOrWhiteSpace(new MinioDocumentStoreOptions().Bucket));

    [Fact]
    public void The_platform_registers_the_store_against_the_Contracts_seam_and_nothing_else()
    {
        // Invariant 6 applied to storage: a module resolves IDocumentStore, never MinioDocumentStore,
        // so swapping S3 or a filesystem in is a DI change with no domain code touched.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGridCorePlatform(
            Configuration(
                ("ConnectionStrings:gridcore", "Host=localhost;Database=gridcore;Username=u;Password=p"),
                ($"{MinioDocumentStoreOptions.SectionName}:Endpoint", "http://localhost:9000"),
                ($"{MinioDocumentStoreOptions.SectionName}:AccessKey", "gridcore"),
                ($"{MinioDocumentStoreOptions.SectionName}:SecretKey", "gridcore-secret")),
            new FakeHostEnvironment(Environments.Development));

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(MinioDocumentStore));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<MinioDocumentStore>(provider.GetRequiredService<IDocumentStore>());
    }
}
