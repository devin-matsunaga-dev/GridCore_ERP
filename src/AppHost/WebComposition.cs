using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;

namespace GridCore.AppHost;

/// <summary>Composes the ASP.NET Core host and the React dev server.</summary>
public static class WebComposition
{
    /// <summary>Resource name of the ASP.NET Core host.</summary>
    public const string WebHostResourceName = "web-host";

    /// <summary>Resource name of the React dev server.</summary>
    public const string WebAppResourceName = "web";

    /// <summary>Directory (relative to the repository root) holding the React app.</summary>
    public const string WebAppDirectoryName = "web";

    /// <summary>
    /// Port the SPA is served on. Fixed, not allocated: the Keycloak realm registers
    /// <c>gridcore-web</c> against <c>http://localhost:5173</c>, and an OIDC client may only
    /// redirect back to a URI it registered. A port Aspire picked at random is a rejected
    /// <c>redirect_uri</c>, so this value, `web/vite.config.ts` and the realm export must agree.
    /// </summary>
    public const int WebAppPort = 5173;

    /// <summary>Health endpoint the host exposes; aggregates the checks registered by the client integrations.</summary>
    public const string HealthEndpointPath = "/health";

    /// <summary>Keycloak's primary endpoint, which the OIDC issuer URL is built from.</summary>
    public const string IdentityEndpointName = "http";

    /// <summary>The endpoint `AddViteApp` creates for the dev server.</summary>
    public const string WebAppEndpointName = "http";

    /// <summary>
    /// Adds the ASP.NET Core host and wires it to every backing service. The host waits for the
    /// infrastructure so a cold `aspire run` does not report a false failure while containers boot.
    /// </summary>
    public static IResourceBuilder<ProjectResource> AddGridCoreWebHost(
        this IDistributedApplicationBuilder builder,
        GridCoreInfrastructure infrastructure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(infrastructure);

        return builder
            .AddProject<Projects.GridCore_Web_Host>(WebHostResourceName)
            .WithReference(infrastructure.Database).WaitFor(infrastructure.Database)
            .WithReference(infrastructure.Cache).WaitFor(infrastructure.Cache)
            .WithReference(infrastructure.Bus).WaitFor(infrastructure.Bus)
            .WithReference(infrastructure.Identity).WaitFor(infrastructure.Identity)
            .WithGridCoreAuthentication(infrastructure.Identity)
            // MinIO is a plain container, so it is passed as configuration rather than a connection string.
            .WithEnvironment("MinIO__Endpoint", infrastructure.ObjectStore.GetEndpoint("api"))
            .WithEnvironment("MinIO__AccessKey", infrastructure.ObjectStoreAccessKey)
            .WithEnvironment("MinIO__SecretKey", infrastructure.ObjectStoreSecretKey)
            .WaitFor(infrastructure.ObjectStore)
            .WithHttpHealthCheck(HealthEndpointPath);
    }

    /// <summary>
    /// Points the host at the Keycloak realm. Configuration only — the host uses plain OIDC bearer
    /// authentication, so another provider is a change of these values and nothing else. The issuer
    /// is the same URL the browser uses, so tokens minted for the SPA validate on the API.
    /// </summary>
    private static IResourceBuilder<ProjectResource> WithGridCoreAuthentication(
        this IResourceBuilder<ProjectResource> webHost,
        IResourceBuilder<KeycloakResource> identity)
    {
        var realmUrl = RealmUrl(identity);

        return webHost
            .WithEnvironment("Authentication__Authority", realmUrl)
            .WithEnvironment("Authentication__Audience", InfrastructureComposition.IdentityApiClientId)
            // Keycloak is served over plain HTTP on the developer's loopback interface.
            .WithEnvironment("Authentication__RequireHttpsMetadata", "false");
    }

    /// <summary>The realm URL both the API and the SPA use, so the issuer matches on both sides.</summary>
    private static ReferenceExpression RealmUrl(IResourceBuilder<KeycloakResource> identity) =>
        ReferenceExpression.Create(
            $"{identity.GetEndpoint(IdentityEndpointName)}/realms/{InfrastructureComposition.IdentityRealmName}");

    /// <summary>
    /// Adds the React dev server when the app exists. `web/` is created in WP-0.6; until then the
    /// resource is skipped so `aspire run` stays green on a fresh clone.
    /// </summary>
    /// <returns>The web app resource, or <see langword="null"/> when `web/` has not been created yet.</returns>
    public static IResourceBuilder<ViteAppResource>? AddGridCoreWebApp(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> webHost,
        GridCoreInfrastructure infrastructure,
        Func<string, bool>? directoryExists = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(webHost);
        ArgumentNullException.ThrowIfNull(infrastructure);

        if (!TryLocateWebApp(builder.AppHostDirectory, directoryExists ?? Directory.Exists, out var appDirectory))
        {
            return null;
        }

        return builder
            .AddViteApp(WebAppResourceName, appDirectory)
            .WithNpm()
            .WithReference(webHost)
            .WaitFor(webHost)
            .WithGridCoreWebAppEndpoint()
            .WithGridCoreWebAppConfiguration(infrastructure);
    }

    /// <summary>
    /// Pins the dev server to <see cref="WebAppPort"/> and takes it out from behind Aspire's
    /// reverse proxy. Both matter: proxied, the browser is handed a randomly allocated port, and
    /// the origin it then sends as <c>redirect_uri</c> is not the one the realm registered.
    /// </summary>
    public static IResourceBuilder<ViteAppResource> WithGridCoreWebAppEndpoint(
        this IResourceBuilder<ViteAppResource> webApp)
    {
        ArgumentNullException.ThrowIfNull(webApp);

        return webApp.WithEndpoint(
            WebAppEndpointName,
            endpoint =>
            {
                endpoint.Port = WebAppPort;
                endpoint.TargetPort = WebAppPort;
                endpoint.IsProxied = false;
            });
    }

    /// <summary>
    /// Hands the SPA its browser-visible configuration. Vite only exposes <c>VITE_</c>-prefixed
    /// variables to the bundle, and the OIDC authority must be the same URL the API validates
    /// tokens against — a different host for login than for validation fails issuer matching.
    /// The API base URL is deliberately absent: the dev server proxies <c>/api</c> to the host, so
    /// the browser stays same-origin and the host needs no CORS policy.
    /// </summary>
    public static IResourceBuilder<ViteAppResource> WithGridCoreWebAppConfiguration(
        this IResourceBuilder<ViteAppResource> webApp,
        GridCoreInfrastructure infrastructure)
    {
        ArgumentNullException.ThrowIfNull(webApp);
        ArgumentNullException.ThrowIfNull(infrastructure);

        return webApp
            .WithEnvironment("VITE_OIDC_AUTHORITY", RealmUrl(infrastructure.Identity))
            .WithEnvironment("VITE_OIDC_CLIENT_ID", InfrastructureComposition.IdentityWebClientId)
            .WithEnvironment("VITE_OIDC_AUDIENCE", InfrastructureComposition.IdentityApiClientId);
    }

    /// <summary>
    /// Resolves `web/` from the AppHost directory (`src/AppHost` → repository root → `web`).
    /// Pure so the "app not created yet" path is unit-testable without touching the filesystem.
    /// </summary>
    public static bool TryLocateWebApp(string appHostDirectory, Func<string, bool> directoryExists, out string appDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostDirectory);
        ArgumentNullException.ThrowIfNull(directoryExists);

        appDirectory = Path.GetFullPath(Path.Combine(appHostDirectory, "..", "..", WebAppDirectoryName));

        return directoryExists(appDirectory);
    }
}
