using Harbor.Build.Meta;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Targets;
/// <summary>
///     Compile target — runs <c>dotnet build</c> against the entire solution
///     in the configured <see cref="BuildConfiguration" />. Uses
///     <c>--no-restore</c> (assumes <see cref="RestoreTarget" /> ran first).
///     Enables parallel build (<c>-m</c>) and binary log (<c>-bl</c>) for
///     diagnostic capture. Dry-run emits only the equivalent argv.
/// </summary>
public static class CompileTarget
{
    /// <summary>
    ///     Executes <c>dotnet build</c> on the given solution using the
    ///     settings from <paramref name="settings" />.
    /// </summary>
    public static void Execute(Solution solution, BuildSettings settings, BuildOutput output)
    {
        var configuration = settings.ConfigurationString;
        var solutionPath = solution.Path.ToString();
        output.Cmd("Compile", ["dotnet", "build", solutionPath, "-c", configuration, "--no-restore", "-p:MaxCpuCount=0"]);
        if (output.IsDryRun)
        {
            return;
        }
        DotNetTasks.DotNetBuild(s => s
            .SetProjectFile(solution)
            .SetConfiguration(configuration)
            .EnableNoRestore()
            .SetProperty("MaxCpuCount", "0")); // 0 = use all CPUs (parallel build)
    }
}
