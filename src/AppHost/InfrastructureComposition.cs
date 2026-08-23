using Aspire.Hosting.ApplicationModel;

namespace GridCore.AppHost;

/// <summary>
/// The backing services every GridCore environment runs against. Held together in one record
/// so the host project can be wired to all of them in a single, greppable place.
/// </summary>
/// <param name="Database">The application database (schema-per-module lives inside it).</param>
/// <param name="Cache">Redis: cache, rate limiting, SignalR backplane.</param>
/// <param name="Bus">RabbitMQ: the transactional-outbox transport (WP-0.5).</param>
/// <param name="Identity">Keycloak: OIDC provider; realm + roles arrive in WP-0.3.</param>
/// <param name="ObjectStore">MinIO: document and report storage.</param>
/// <param name="ObjectStoreAccessKey">MinIO root user, handed to the host as well as the container.</param>
/// <param name="ObjectStoreSecretKey">MinIO root password, handed to the host as well as the container.</param>
public sealed record GridCoreInfrastructure(
    IResourceBuilder<PostgresDatabaseResource> Database,
    IResourceBuilder<RedisResource> Cache,
    IResourceBuilder<RabbitMQServerResource> Bus,
    IResourceBuilder<KeycloakResource> Identity,
    IResourceBuilder<ContainerResource> ObjectStore,
    IResourceBuilder<ParameterResource> ObjectStoreAccessKey,
    IResourceBuilder<ParameterResource> ObjectStoreSecretKey);

/// <summary>Composes the backing services orchestrated by the AppHost.</summary>
public static class InfrastructureComposition
{
    /// <summary>Resource name of the Postgres server container.</summary>
    public const string PostgresResourceName = "postgres";

    /// <summary>Resource name of the application database created on that server.</summary>
    public const string DatabaseResourceName = "gridcore";

    /// <summary>Resource name of the Redis container.</summary>
    public const string CacheResourceName = "redis";

    /// <summary>Resource name of the RabbitMQ container.</summary>
    public const string BusResourceName = "rabbitmq";

    /// <summary>Resource name of the Keycloak container.</summary>
    public const string IdentityResourceName = "keycloak";

    /// <summary>Resource name of the MinIO container.</summary>
    public const string ObjectStoreResourceName = "minio";

    // MinIO has no first-party Aspire integration, so it is composed as a plain container
    // rather than pulling in a pre-release community package. Pin the tag: "latest" would
    // silently change the object store under a demo.
    private const string MinioImage = "minio/minio";
    private const string MinioTag = "RELEASE.2025-09-07T16-13-09Z";
    private const int MinioApiPort = 9000;
    private const int MinioConsolePort = 9001;

    /// <summary>
    /// Adds Postgres, Redis, RabbitMQ, Keycloak and MinIO to the application model.
    /// Credentials come from named parameters rather than per-run generated values because
    /// Postgres, RabbitMQ, Keycloak and MinIO persist their users into their data volumes — a
    /// regenerated password would lock the AppHost out of the volume it created on the previous
    /// run. Redis is the exception (nothing but RDB data in its volume), so it keeps Aspire's
    /// generated password.
    /// </summary>
    public static GridCoreInfrastructure AddGridCoreInfrastructure(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var postgresUser = builder.AddParameter("postgres-username");
        var postgresPassword = builder.AddParameter("postgres-password", secret: true);
        var database = builder
            .AddPostgres(PostgresResourceName, postgresUser, postgresPassword)
            .WithDataVolume()
            .AddDatabase(DatabaseResourceName);

        var cache = builder
            .AddRedis(CacheResourceName)
            .WithDataVolume();

        var busUser = builder.AddParameter("rabbitmq-username");
        var busPassword = builder.AddParameter("rabbitmq-password", secret: true);
        var bus = builder
            .AddRabbitMQ(BusResourceName, busUser, busPassword)
            .WithDataVolume()
            .WithManagementPlugin();

        var identityAdmin = builder.AddParameter("keycloak-admin-username");
        var identityPassword = builder.AddParameter("keycloak-admin-password", secret: true);
        var identity = builder
            .AddKeycloak(IdentityResourceName, adminUsername: identityAdmin, adminPassword: identityPassword)
            .WithDataVolume();

        var objectStoreAccessKey = builder.AddParameter("minio-access-key");
        var objectStoreSecretKey = builder.AddParameter("minio-secret-key", secret: true);
        var objectStore = builder.AddGridCoreObjectStore(objectStoreAccessKey, objectStoreSecretKey);

        return new GridCoreInfrastructure(
            database, cache, bus, identity, objectStore, objectStoreAccessKey, objectStoreSecretKey);
    }

    private static IResourceBuilder<ContainerResource> AddGridCoreObjectStore(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> accessKey,
        IResourceBuilder<ParameterResource> secretKey)
    {
        return builder
            .AddContainer(ObjectStoreResourceName, MinioImage, MinioTag)
            .WithArgs("server", "/data", "--console-address", $":{MinioConsolePort}")
            .WithEnvironment("MINIO_ROOT_USER", accessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", secretKey)
            .WithHttpEndpoint(targetPort: MinioApiPort, name: "api")
            .WithHttpEndpoint(targetPort: MinioConsolePort, name: "console")
            .WithVolume("/data")
            .WithHttpHealthCheck("/minio/health/live", endpointName: "api");
    }
}
