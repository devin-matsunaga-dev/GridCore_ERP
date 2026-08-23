namespace GridCore.IntegrationTests;

/// <summary>
/// Gate-tier suite. Everything here is tagged Category=Integration so the fast per-package
/// loop filters it out. The shared Testcontainers collection fixture + Respawn scaffolding
/// lands in WP-0.7; until then this file only proves the trait filter is wired.
/// </summary>
[Trait("Category", "Integration")]
public class HostSmokeTests
{
    [Fact]
    public void Host_assembly_is_reachable_from_the_gate_suite()
    {
        Assert.Equal("GridCore.Web.Host", typeof(Program).Assembly.GetName().Name);
    }
}
