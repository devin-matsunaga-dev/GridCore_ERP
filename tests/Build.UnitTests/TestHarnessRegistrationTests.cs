using System.Text.Json;

namespace GridCore.Build.UnitTests;

/// <summary>
/// The fast loop runs <c>tests/UnitTests.slnf</c>, and a project missing from that filter is not
/// reported as an error — it is simply never run, so the tests in it pass by not existing. These
/// assertions are the thing that notices.
/// </summary>
public class TestHarnessRegistrationTests
{
    private const string SolutionFilter = "tests/UnitTests.slnf";
    private const string Solution = "GridCore.slnx";

    /// <summary>
    /// The projects the filter actually lists, parsed rather than string-matched: a .slnf is JSON
    /// and spells its paths with escaped Windows separators, so the file's raw text does not
    /// contain the paths it names.
    /// </summary>
    private static IReadOnlyList<string> FilteredProjects()
    {
        using var filter = JsonDocument.Parse(RepositoryFiles.ReadAllText(SolutionFilter));

        return [.. filter.RootElement
            .GetProperty("solution")
            .GetProperty("projects")
            .EnumerateArray()
            .Select(project => project.GetString()!.Replace('\\', '/'))];
    }

    private static IReadOnlyList<string> UnitTestProjects() =>
        [.. RepositoryFiles.ProjectFiles("tests")
            .Where(path => path.EndsWith("UnitTests.csproj", StringComparison.Ordinal))];

    [Fact]
    public void Every_unit_test_project_is_listed_in_the_fast_solution_filter()
    {
        var filtered = FilteredProjects();

        var missing = UnitTestProjects()
            .Where(project => !filtered.Contains(project, StringComparer.Ordinal))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"These unit-test projects are not in {SolutionFilter}, so the fast loop silently skips "
            + $"them: {string.Join(", ", missing)}");
    }

    [Fact]
    public void The_fast_solution_filter_lists_nothing_that_no_longer_exists()
    {
        // The other direction of the same rule: a filter naming a deleted project fails the whole
        // run with a restore error, which reads as a broken machine rather than a stale file.
        var missing = FilteredProjects()
            .Where(project => !File.Exists(Path.Combine(RepositoryFiles.Root, project)))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"{SolutionFilter} names projects that do not exist: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_test_project_is_listed_in_the_solution()
    {
        var solution = RepositoryFiles.ReadAllText(Solution);

        var missing = RepositoryFiles.ProjectFiles("tests")
            .Where(project => !solution.Contains(project, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"These test projects are not in {Solution}: {string.Join(", ", missing)}");
    }

    [Fact]
    public void The_fast_solution_filter_excludes_the_gate_tier()
    {
        // Failure path for the rule above: containers must never be reachable from the fast loop,
        // however the filter is edited.
        Assert.DoesNotContain(
            FilteredProjects(),
            project => project.Contains("IntegrationTests/", StringComparison.Ordinal));
    }
}
