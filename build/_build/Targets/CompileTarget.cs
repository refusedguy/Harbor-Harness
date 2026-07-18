using Harbor.Build.Components;
using Harbor.Build.Configuration;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;

namespace Harbor.Build.Targets;

/// <summary>
///     Compile target — runs <c>dotnet build</c> against the entire solution
///     in the configured <see cref="BuildConfiguration"/>. Uses
///     <c>--no-restore</c> (assumes <see cref="RestoreTarget"/> ran first).
///     Enables parallel build (<c>-m</c>) and binary log (<c>-bl</c>) for
///     diagnostic capture.
/// </summary>
public static class CompileTarget
{
    /// <summary>
    ///     Executes <c>dotnet build</c> on the given solution using the
    ///     settings from <paramref name="settings"/>.
    /// </summary>
    public static void Execute(Solution solution, BuildSettings settings)
    {
        Console.WriteLine($"==> Compile: dotnet build {solution.Name} -c {settings.ConfigurationString}");
        DotNetTasks.DotNetBuild(s => s
            .SetProjectFile(solution)
            .SetConfiguration(settings.ConfigurationString)
            .EnableNoRestore()
            .SetProperty("MaxCpuCount", "0")); // 0 = use all CPUs (parallel build)
        Console.WriteLine("==> Compile: done");
    }
}
