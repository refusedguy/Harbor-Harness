using Harbor.Build.Meta;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Targets;
/// <summary>
///     Test target — runs every unit-test project in the solution as a plain
///     executable (<c>dotnet run --project</c>), one process per project,
///     sequentially. Failures are aggregated: every project is attempted and
///     the target throws listing all failed projects at the end.
/// </summary>
/// <remarks>
///     <para>
///         <c>dotnet test</c> is deliberately NOT used: its
///         Microsoft.Testing.Platform bridge rejects the VSTest-era options
///         (<c>--logger</c>, <c>--filter</c>) our old commands passed, and that
///         is the platform's <c>invalid command-line arguments</c> exit code 5
///         rather than evidence of zero discovered tests (a run that genuinely
///         discovers no tests exits 8). The same assemblies run green via
///         direct host execution. MTP args forward after <c>--</c>, including
///         <c>--minimum-expected-tests 1</c> so a silent zero-test regression
///         fails loudly.
///     </para>
///     <para>
///         Environment-bound suites are excluded from the local gate and run in
///         CI instead (see <c>.github/workflows/ci.yml</c> shards):
///         <c>*E2E*</c> (live providers / headed UI), <c>LoadTests</c> (slow),
///         <c>PerfTests</c>/<c>Benchmarks</c> (renderer-perf-gate / benchmark
///         workflows), <c>*PtyTests</c> (need a PTY + prebuilt CLI),
///         <c>TestKit</c>/<c>E2E.Framework</c> (harness libraries, no tests).
///     </para>
/// </remarks>
public static class TestTarget
{
    private static readonly string[] ExcludedSubstrings =
    [
        "E2E",
        "LoadTests",
        "PerfTests",
        "Benchmarks",
        "TestKit",
        "PtyTests",
    ];

    /// <summary>
    ///     Executes the per-project test runs for the given solution.
    /// </summary>
    public static void Execute(Solution solution, BuildSettings settings, BuildOutput output)
    {
        var configuration = settings.ConfigurationString;
        var projects = solution.AllProjects
            .Where(p => p.Name.EndsWith("Tests", StringComparison.Ordinal)
                && !ExcludedSubstrings.Any(s => p.Name.Contains(s, StringComparison.Ordinal)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        output.Info("Test", $"Running {projects.Length} test project(s) sequentially (--minimum-expected-tests 1).");

        var failed = new List<string>();
        foreach (var project in projects)
        {
            var projectPath = project.Path.ToString();
            output.Cmd("Test", ["dotnet", "run", "--project", projectPath, "-c", configuration, "--no-build", "--", "--minimum-expected-tests", "1"]);
            if (output.IsDryRun)
            {
                continue;
            }

            try
            {
                DotNetTasks.DotNetRun(s => s
                    .SetProjectFile(projectPath)
                    .SetConfiguration(configuration)
                    .EnableNoBuild()
                    .SetApplicationArguments("--minimum-expected-tests 1"));
                output.Info("Test", $"PASS {project.Name}");
            }
            catch (Exception ex)
            {
                output.Error("Test", $"FAIL {project.Name}: {ex.Message.Split('\n')[0]}");
                failed.Add(project.Name);
            }
        }

        if (failed.Count > 0)
            throw new InvalidOperationException(
                $"Test target failed for {failed.Count} project(s): {string.Join(", ", failed)}.");
    }
}
