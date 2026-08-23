using GridCore.Platform.Security;

namespace GridCore.Platform.UnitTests.Security;

public class GridCoreAuthenticationOptionsTests
{
    private static GridCoreAuthenticationOptions Valid() => new()
    {
        Authority = "http://localhost:8080/realms/gridcore",
        Audience = "gridcore-api",
    };

    [Fact]
    public void Valid_options_pass()
    {
        Valid().Validate();
    }

    [Fact]
    public void Defaults_target_keycloak_realm_roles_and_require_https()
    {
        var options = new GridCoreAuthenticationOptions();

        Assert.Equal("realm_access.roles", options.RolesClaimPath);
        Assert.True(options.RequireHttpsMetadata);
    }

    [Fact]
    public void A_missing_authority_is_rejected_by_name()
    {
        var options = Valid();
        options.Authority = "";

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Authentication:Authority", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/realms/gridcore")]
    [InlineData("realms/gridcore")]
    [InlineData("ftp://identity.example/realms/gridcore")]
    public void An_authority_that_is_not_an_http_url_is_rejected(string authority)
    {
        var options = Valid();
        options.Authority = authority;

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("absolute http(s) URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_audience_is_rejected_by_name()
    {
        var options = Valid();
        options.Audience = " ";

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Authentication:Audience", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_roles_claim_path_is_rejected_by_name()
    {
        var options = Valid();
        options.RolesClaimPath = "";

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Authentication:RolesClaimPath", ex.Message, StringComparison.Ordinal);
    }
}
