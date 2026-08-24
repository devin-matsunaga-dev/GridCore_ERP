namespace GridCore.Build.UnitTests;

/// <summary>
/// CONVENTIONS.md's ⚡ speed rules are only worth anything if CI obeys them, and a workflow file
/// is not compiled — a wrong flag there is discovered as a suite that quietly takes an hour. These
/// assertions hold the pipeline to the rules that matter.
/// </summary>
public class ContinuousIntegrationTests
{
    private const string Workflow = ".github/workflows/ci.yml";
    private const string ReleaseWorkflow = ".github/workflows/release.yml";

    private static readonly string Ci = RepositoryFiles.ReadYamlDirectives(Workflow);

    [Fact]
    public void The_test_run_is_never_forced_onto_a_single_core()
    {
        // The one flag that made the previous project's suite take hours. It must never come back,
        // in any casing MSBuild would accept.
        Assert.DoesNotContain("maxcpucount:1", Ci, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MaxCpuCount=1", Ci, StringComparison.Ordinal);

        // 0 means "one worker per core" — the deliberate opposite.
        Assert.Contains("RunConfiguration.MaxCpuCount=0", Ci, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fast_job_runs_the_unit_filter_and_nothing_from_the_gate_tier()
    {
        Assert.Contains("tests/UnitTests.slnf", Ci, StringComparison.Ordinal);
        Assert.Contains("\"Category!=Integration\"", Ci, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fast_job_builds_once_and_then_stops_building()
    {
        // Rule B: rebuilding per test project is the second-biggest cost after single-core runs.
        Assert.Contains("--no-build --no-restore", Ci, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gate_tier_runs_as_its_own_job_behind_the_fast_ones()
    {
        Assert.Contains("\"Category=Integration\"", Ci, StringComparison.Ordinal);
        Assert.Contains("needs: [dotnet-unit, web]", Ci, StringComparison.Ordinal);
    }

    [Fact]
    public void The_web_tier_installs_from_the_lockfile_and_runs_vitest_once()
    {
        // `npm install` would rewrite the lockfile instead of failing on drift, and `vitest`
        // without --run leaves the job sitting in watch mode until the timeout kills it.
        Assert.Contains("npm ci", Ci, StringComparison.Ordinal);
        Assert.Contains("npm run test -- --run", Ci, StringComparison.Ordinal);
        Assert.DoesNotContain("npm install", Ci, StringComparison.Ordinal);
    }

    [Fact]
    public void The_web_lint_step_actually_lints()
    {
        Assert.Contains("npm run lint", Ci, StringComparison.Ordinal);

        var package = RepositoryFiles.ReadAllText("web/package.json");

        // `lint` was `tsc -b --noEmit` alone until WP-0.7 — a type check, not a linter. Both run
        // now, and the type check has to stay: oxlint is type-unaware, so it cannot replace it.
        Assert.Contains("\"lint\": \"oxlint && tsc -b --noEmit\"", package, StringComparison.Ordinal);
    }

    [Fact]
    public void Images_are_pushed_only_for_a_version_tag()
    {
        var release = RepositoryFiles.ReadYamlDirectives(ReleaseWorkflow);

        Assert.Contains("tags: ['v*']", release, StringComparison.Ordinal);
        Assert.Contains("ghcr.io", release, StringComparison.Ordinal);

        // Failure path: a branch push must never publish an image, so CI carries no registry login
        // and the release workflow carries no branch trigger.
        Assert.DoesNotContain("branches:", release, StringComparison.Ordinal);
        Assert.DoesNotContain("docker login", Ci, StringComparison.Ordinal);
    }
}
