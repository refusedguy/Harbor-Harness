using Harbor.Build.Components;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Targets;
/// <summary>
///     Architecture-test target — runs only the
///     <c>Harbor.Architecture.Tests</c> project. Used to validate layer
///     dependencies and namespace containment without running the full
///     test suite (faster CI signal).
/// </summary>
public static class ArchitectureTestTarget
{
    /// <summary>
    ///     Executes <c>dotnet test</c> against the architecture test project.
    /// </summary>
    public static void Execute(ArtifactPathResolver resolver, BuildSettings settings)
    {
        var csproj = resolver.TestsDirectory / "Harbor.Architecture.Tests" / "Harbor.Architecture.Tests.csproj";
        Console.WriteLine($"==> ArchitectureTests: dotnet test {csproj.Name}");
        DotNetTasks.DotNetTest(s => s
            .SetProjectFile(csproj)
            .SetConfiguration(settings.ConfigurationString)
            .EnableNoRestore()
            .EnableNoBuild());
        Console.WriteLine("==> ArchitectureTests: done");
    }
}
