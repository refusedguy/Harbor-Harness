using Harbor.Build.Components;
using Harbor.Build.Meta;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Targets;
/// <summary>
///     Architecture-test target — runs only the
///     <c>Harbor.Architecture.Tests</c> project. Used to validate layer
///     dependencies and namespace containment without running the full
///     test suite (faster CI signal).
///     Dry-run emits only the equivalent argv.
/// </summary>
public static class ArchitectureTestTarget
{
    /// <summary>
    ///     Executes <c>dotnet test</c> against the architecture test project.
    /// </summary>
    public static void Execute(ArtifactPathResolver resolver, BuildSettings settings, BuildOutput output)
    {
        var csproj = resolver.TestsDirectory / "Harbor.Architecture.Tests" / "Harbor.Architecture.Tests.csproj";
        var configuration = settings.ConfigurationString;
        output.Cmd("ArchitectureTests", ["dotnet", "test", csproj.ToString(), "-c", configuration, "--no-restore", "--no-build"]);
        if (output.IsDryRun)
        {
            return;
        }
        DotNetTasks.DotNetTest(s => s
            .SetProjectFile(csproj)
            .SetConfiguration(configuration)
            .EnableNoRestore()
            .EnableNoBuild());
    }
}
