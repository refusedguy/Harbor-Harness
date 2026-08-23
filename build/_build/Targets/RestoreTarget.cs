using Harbor.Build.Meta;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Targets;
/// <summary>
///     Restore target — runs <c>dotnet restore</c> against the entire
///     solution. Uses <c>--locked-mode</c> when a <c>packages.lock.json</c>
///     exists (CI reproducibility); otherwise does a normal restore.
///     Dry-run emits only the equivalent argv.
/// </summary>
public static class RestoreTarget
{
    /// <summary>
    ///     Executes <c>dotnet restore</c> on the given solution.
    /// </summary>
    public static void Execute(Solution solution, BuildOutput output)
    {
        var solutionPath = solution.Path.ToString();
        output.Cmd("Restore", ["dotnet", "restore", solutionPath]);
        if (output.IsDryRun)
        {
            return;
        }
        DotNetTasks.DotNetRestore(s => s
            .SetProjectFile(solution)
            .SetVerbosity(DotNetVerbosity.minimal));
    }
}
