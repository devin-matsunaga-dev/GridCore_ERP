namespace GridCore.Build.UnitTests;

/// <summary>
/// Reads the repository's own configuration files. The tests here assert on checked-in build and
/// CI configuration rather than on compiled code, so they need the source tree, not the output
/// directory — the root is found by walking up to the solution file.
/// </summary>
public static class RepositoryFiles
{
    /// <summary>The file that marks the repository root.</summary>
    private const string SolutionFileName = "GridCore.slnx";

    /// <summary>Absolute path of the repository root.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>Reads a repository file by its root-relative path.</summary>
    public static string ReadAllText(string relativePath) =>
        File.ReadAllText(Path.Combine(Root, relativePath));

    /// <summary>
    /// Reads a YAML file with its comment lines removed. An assertion that a flag is <i>absent</i>
    /// has to read the directives only: the comments in these files name the flags they warn
    /// against, and a test that trips over the warning about a mistake instead of the mistake is
    /// worse than no test.
    /// </summary>
    public static string ReadYamlDirectives(string relativePath) =>
        string.Join(
            '\n',
            ReadAllText(relativePath)
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith('#')));

    /// <summary>Every project file under a root-relative directory, as root-relative paths.</summary>
    public static IReadOnlyList<string> ProjectFiles(string relativeDirectory) =>
        [.. Directory
            .EnumerateFiles(Path.Combine(Root, relativeDirectory), "*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                $"Could not find {SolutionFileName} above {AppContext.BaseDirectory}.");
    }
}
