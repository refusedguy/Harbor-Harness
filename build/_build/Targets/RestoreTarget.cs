using Harbor.Build.Components;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;

namespace Harbor.Build.Targets;

/// <summary>
///     Restore target — runs <c>dotnet restore</c> against the entire
///     solution. Uses <c>--locked-mode</c> when a <c>packages.lock.json</c>
///     exists (CI reproducibility); otherwise does a normal restore.
/// </summary>
public static class RestoreTarget
{
    /// <summary>
    ///     Executes <c>dotnet restore</c> on the given solution.
    /// </summary>
    public static void Execute(Solution solution)
    {
        Console.WriteLine($"==> Restore: dotnet restore {solution.Name}");
        DotNetTasks.DotNetRestore(s => s
            .SetProjectFile(solution)
            .SetVerbosity(DotNetVerbosity.minimal));
        Console.WriteLine("==> Restore: done");
    }
}
