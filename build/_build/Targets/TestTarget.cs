using Harbor.Build.Components;
using Harbor.Build.Configuration;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;

namespace Harbor.Build.Targets;

/// <summary>
///     Test target — runs <c>dotnet test</c> against the entire solution with
///     <c>--no-build</c> (assumes <see cref="CompileTarget"/> ran first).
///     Enables parallel test execution via <c>MaxCpuCount</c>.
/// </summary>
public static class TestTarget
{
    /// <summary>
    ///     Executes <c>dotnet test</c> on the given solution.
    /// </summary>
    public static void Execute(Solution solution, BuildSettings settings)
    {
        Console.WriteLine($"==> Test: dotnet test {solution.Name} -c {settings.ConfigurationString}");
        DotNetTasks.DotNetTest(s => s
            .SetProjectFile(solution)
            .SetConfiguration(settings.ConfigurationString)
            .EnableNoRestore()
            .EnableNoBuild()
            .SetProperty("MaxCpuCount", "4"));
        Console.WriteLine("==> Test: done");
    }
}
