using System.Text.Json;
using GridCore.AppHost;
using GridCore.Platform.Security;

namespace GridCore.AppHost.UnitTests;

/// <summary>
/// The realm export is the identity provider's copy of GridCore's roles. These assertions keep it
/// in step with <see cref="GridCoreRoles"/> — a role added in code but not in the realm would only
/// show up as a mysterious 403 at demo time.
/// </summary>
public class KeycloakRealmExportTests
{
    private static readonly JsonDocument Realm = LoadRealm();

    private static JsonDocument LoadRealm()
    {
        var path = Path.Combine(AppContext.BaseDirectory, InfrastructureComposition.IdentityRealmImportPath, "gridcore-realm.json");

        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static IReadOnlyList<string> RealmRoleNames() =>
        [.. Realm.RootElement.GetProperty("roles").GetProperty("realm").EnumerateArray()
            .Select(role => role.GetProperty("name").GetString()!)];

    private static IReadOnlyList<JsonElement> Users() =>
        [.. Realm.RootElement.GetProperty("users").EnumerateArray()];

    private static JsonElement Client(string clientId) =>
        Realm.RootElement.GetProperty("clients").EnumerateArray()
            .Single(client => client.GetProperty("clientId").GetString() == clientId);

    [Fact]
    public void The_realm_is_the_one_the_host_is_pointed_at()
    {
        Assert.Equal(InfrastructureComposition.IdentityRealmName, Realm.RootElement.GetProperty("realm").GetString());
        Assert.True(Realm.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void The_realm_declares_exactly_the_eight_gridcore_roles()
    {
        Assert.Equal(
            GridCoreRoles.All.OrderBy(role => role, StringComparer.Ordinal),
            RealmRoleNames().OrderBy(role => role, StringComparer.Ordinal));
    }

    [Fact]
    public void There_is_one_test_user_per_role()
    {
        var assignedRoles = Users()
            .SelectMany(user => user.GetProperty("realmRoles").EnumerateArray().Select(role => role.GetString()!))
            .OrderBy(role => role, StringComparer.Ordinal);

        Assert.Equal(GridCoreRoles.All.OrderBy(role => role, StringComparer.Ordinal), assignedRoles);
    }

    [Fact]
    public void Every_test_user_is_enabled_and_has_a_permanent_password()
    {
        Assert.All(Users(), user =>
        {
            Assert.True(user.GetProperty("enabled").GetBoolean());

            var credential = user.GetProperty("credentials").EnumerateArray().Single();
            Assert.Equal("password", credential.GetProperty("type").GetString());
            Assert.False(credential.GetProperty("temporary").GetBoolean());
        });
    }

    [Fact]
    public void The_spa_client_mints_tokens_audienced_to_the_api_client()
    {
        var mapper = Client(InfrastructureComposition.IdentityWebClientId)
            .GetProperty("protocolMappers").EnumerateArray()
            .Single(m => m.GetProperty("protocolMapper").GetString() == "oidc-audience-mapper");

        Assert.Equal(
            InfrastructureComposition.IdentityApiClientId,
            mapper.GetProperty("config").GetProperty("included.client.audience").GetString());
        Assert.Equal("true", mapper.GetProperty("config").GetProperty("access.token.claim").GetString());
    }

    [Fact]
    public void The_spa_client_is_a_public_pkce_client()
    {
        var spa = Client(InfrastructureComposition.IdentityWebClientId);

        Assert.True(spa.GetProperty("publicClient").GetBoolean());
        Assert.True(spa.GetProperty("standardFlowEnabled").GetBoolean());
        Assert.False(spa.GetProperty("implicitFlowEnabled").GetBoolean());
        Assert.Equal("S256", spa.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString());
    }

    [Fact]
    public void The_api_client_initiates_no_flows_of_its_own()
    {
        var api = Client(InfrastructureComposition.IdentityApiClientId);

        Assert.False(api.GetProperty("standardFlowEnabled").GetBoolean());
        Assert.False(api.GetProperty("implicitFlowEnabled").GetBoolean());
        Assert.False(api.GetProperty("directAccessGrantsEnabled").GetBoolean());
        Assert.False(api.GetProperty("serviceAccountsEnabled").GetBoolean());
    }
}
