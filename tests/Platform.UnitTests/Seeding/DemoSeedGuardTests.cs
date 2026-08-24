using GridCore.Platform.Seeding;
using Microsoft.Extensions.Hosting;

namespace GridCore.Platform.UnitTests.Seeding;

/// <summary>
/// ARCHITECTURE.md invariant 8: demo data is Development-only. The asymmetry matters as much as the
/// rule — configuration may turn seeding off, and must never be able to turn it on.
/// </summary>
public class DemoSeedGuardTests
{
    [Fact]
    public void Development_seeds_by_default()
    {
        Assert.True(DemoSeedGuard.IsAllowed(new FakeHostEnvironment(Environments.Development), configured: null));
    }

    [Fact]
    public void Development_can_be_told_not_to_seed()
    {
        Assert.False(DemoSeedGuard.IsAllowed(new FakeHostEnvironment(Environments.Development), configured: false));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Demo")]
    public void No_other_environment_seeds_by_default(string environmentName)
    {
        Assert.False(DemoSeedGuard.IsAllowed(new FakeHostEnvironment(environmentName), configured: null));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Demo")]
    public void No_configuration_can_switch_seeding_on_outside_development(string environmentName)
    {
        // The failure path that matters: someone sets Platform__SeedDemoData=true against a real
        // database. The environment decides, and the setting only ever narrows it.
        Assert.False(DemoSeedGuard.IsAllowed(new FakeHostEnvironment(environmentName), configured: true));
    }

    [Fact]
    public void Ensuring_it_is_allowed_throws_outside_development_and_names_the_environment()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => DemoSeedGuard.EnsureAllowed(new FakeHostEnvironment(Environments.Production)));

        Assert.Contains(Environments.Production, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensuring_it_is_allowed_passes_in_development()
    {
        DemoSeedGuard.EnsureAllowed(new FakeHostEnvironment(Environments.Development));
    }
}
