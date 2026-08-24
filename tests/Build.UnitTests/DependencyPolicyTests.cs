namespace GridCore.Build.UnitTests;

/// <summary>
/// MassTransit 9.x moved from Apache-2.0 to a commercial licence, so WP-0.5 pinned the project to
/// the 8.x line. A pin is only half of that decision: something has to stop an automated bump from
/// quietly relicensing the product's messaging layer, and a merged Dependabot PR is exactly how
/// that would happen.
/// </summary>
public class DependencyPolicyTests
{
    private const string Packages = "Directory.Packages.props";
    private const string Dependabot = ".github/dependabot.yml";

    private static readonly string[] MassTransitPackages =
        ["MassTransit", "MassTransit.RabbitMQ", "MassTransit.EntityFrameworkCore"];

    [Fact]
    public void Every_massTransit_package_is_pinned_to_the_apache_licensed_line()
    {
        var packages = RepositoryFiles.ReadAllText(Packages);

        foreach (var package in MassTransitPackages)
        {
            Assert.Contains(
                $"""<PackageVersion Include="{package}" Version="8.""",
                packages,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Dependabot_is_told_not_to_offer_the_commercial_line()
    {
        var dependabot = RepositoryFiles.ReadYamlDirectives(Dependabot);

        foreach (var package in MassTransitPackages)
        {
            Assert.Contains($"dependency-name: {package}\n", dependabot, StringComparison.Ordinal);
        }

        // One hold per package, each naming the 9.x boundary.
        Assert.Equal(
            MassTransitPackages.Length,
            dependabot.Split("versions: ['>=9.0.0']", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Dependabot_watches_the_three_places_this_repository_takes_dependencies()
    {
        var dependabot = RepositoryFiles.ReadYamlDirectives(Dependabot);

        Assert.Contains("package-ecosystem: nuget", dependabot, StringComparison.Ordinal);
        Assert.Contains("package-ecosystem: npm", dependabot, StringComparison.Ordinal);
        Assert.Contains("package-ecosystem: github-actions", dependabot, StringComparison.Ordinal);
    }
}
