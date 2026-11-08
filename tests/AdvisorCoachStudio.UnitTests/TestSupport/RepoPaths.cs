using System.Runtime.CompilerServices;

namespace AdvisorCoachStudio.UnitTests.TestSupport;

/// <summary>
/// Locates checked-in repository files. Build output may live outside the repository (--artifacts-path), so the
/// repository root is found from this source file's compile-time path, falling back to the output directory.
/// </summary>
internal static class RepoPaths
{
    private const string SolutionFile = "AdvisorCoachStudio.sln";

    private static readonly Lazy<string> Root = new(FindRoot);

    public static string RepositoryRoot => Root.Value;

    public static string WebProject => Path.Combine(RepositoryRoot, "src", "AdvisorCoachStudio.Web");

    public static string ScenariosDirectory => Path.Combine(WebProject, "Scenarios");

    public static string ReportSchemaFile => Path.Combine(WebProject, "Schemas", "coaching-report-content.v1.schema.json");

    public static string ApplicationProtocolDoc => Path.Combine(RepositoryRoot, "docs", "application-protocol.md");

    /// <summary>Synthetic provider fixtures copied to the test output directory.</summary>
    public static string GaEventFixturesDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ga-events");

    private static string FindRoot() =>
        FindRootFrom(SourceDirectory()) ??
        FindRootFrom(AppContext.BaseDirectory) ??
        FindRootFrom(Directory.GetCurrentDirectory()) ??
        throw new InvalidOperationException($"Could not locate the repository root ({SolutionFile}).");

    private static string? FindRootFrom(string? start)
    {
        for (var directory = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFile)))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static string? SourceDirectory([CallerFilePath] string path = "") =>
        string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
}
