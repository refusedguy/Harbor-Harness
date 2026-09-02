using Harbor.Build.Meta;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Targets;
/// <summary>
///     Test target — runs <c>dotnet test</c> against the entire solution with
///     <c>--no-build</c> (assumes <see cref="CompileTarget" /> ran first).
///     Enables parallel test execution via <c>MaxCpuCount</c>.
///     Dry-run emits only the equivalent argv.
/// </summary>
public static class TestTarget
{
    /// <summary>
    ///     Executes <c>dotnet test</c> on the given solution.
    /// </summary>
    public static void Execute(Solution solution, BuildSettings settings, BuildOutput output)
    {
        var configuration = settings.ConfigurationString;
        var solutionPath = solution.Path.ToString();
        output.Cmd("Test", ["dotnet", "test", solutionPath, "-c", configuration, "--no-restore", "--no-build", "-p:MaxCpuCount=4"]);
        if (output.IsDryRun)
        {
            return;
        }
        DotNetTasks.DotNetTest(s => s
            .SetProjectFile(solution)
            .SetConfiguration(configuration)
            .EnableNoRestore()
            .EnableNoBuild()
            .SetProperty("MaxCpuCount", "4"));
    }
}
