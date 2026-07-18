using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;

namespace Harbor.Build;

/// <summary>
///     Build configuration enum. NUKE 9.x removed the built-in <c>Configuration</c>
///     class — consumers define their own enum (Debug/Release is the conventional
///     minimum). The <c>[Parameter]</c> attribute wires up command-line / env-var
///     overrides, e.g. <c>./build.sh Compile --configuration Debug</c>.
/// </summary>
public enum Configuration
{
    Debug,
    Release
}

/// <summary>
///     NUKE build entry point for the Harbor solution.
/// </summary>
/// <remarks>
///     <para>
///         Targets (run via <c>./build.sh &lt;Target&gt;</c> or <c>.\build.ps1 &lt;Target&gt;</c>):
///     </para>
///     <list type="table">
///         <item><term><see cref="Clean"/></term><description>Delete bin/obj under src/ apps/ tests/ samples/.</description></item>
///         <item><term><see cref="Restore"/></term><description>dotnet restore the solution.</description></item>
///         <item><term><see cref="Compile"/></term><description>dotnet build the solution in <c>Release</c>.</description></item>
///         <item><term><see cref="Test"/></term><description>dotnet test the solution (no rebuild).</description></item>
///         <item><term><see cref="ArchitectureTests"/></term><description>Run only <c>Harbor.Architecture.Tests</c>.</description></item>
///         <item><term><see cref="PublishCliMinimal"/></term><description>Publish CLI with <c>HARBOR_MINIMAL=true</c> → <c>artifacts/cli-minimal</c>.</description></item>
///         <item><term><see cref="PublishCliFull"/></term><description>Publish CLI with full feature set → <c>artifacts/cli-full</c>.</description></item>
///         <item><term><see cref="PublishSingleFile"/></term><description>Publish self-contained single-file CLI → <c>artifacts/cli-singlefile</c>.</description></item>
///         <item><term><see cref="PublishAot"/></term><description>Experimental native AOT publish (Linux x64 only).</description></item>
///         <item><term><see cref="PublishAvalonia"/></term><description>Publish the Avalonia desktop app → <c>artifacts/avalonia</c>.</description></item>
///         <item><term><see cref="PublishBlazor"/></term><description>Publish the Blazor Server app → <c>artifacts/blazor</c>.</description></item>
///         <item><term><see cref="PublishAll"/></term><description>Run every publish target.</description></item>
///     </list>
///     <para>
///         See <c>docs/BUILD_SYSTEM.md</c> for the full guide, CI integration
///         patterns, and the conditional CLI variant matrix.
///     </para>
/// </remarks>
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build")]
    readonly Configuration Configuration = Configuration.Release;

    string ConfigurationString => Configuration.ToString();

    [Solution("Harbor.slnx")]
    readonly Solution Solution;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath AppsDirectory => RootDirectory / "apps";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath SamplesDirectory => RootDirectory / "samples";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").DeleteDirectories();
            AppsDirectory.GlobDirectories("**/bin", "**/obj").DeleteDirectories();
            TestsDirectory.GlobDirectories("**/bin", "**/obj").DeleteDirectories();
            SamplesDirectory.GlobDirectories("**/bin", "**/obj").DeleteDirectories();
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetTasks.DotNetRestore(s => s.SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetTasks.DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(ConfigurationString)
                .EnableNoRestore());
        });

    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTasks.DotNetTest(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(ConfigurationString)
                .EnableNoRestore()
                .EnableNoBuild());
        });

    Target ArchitectureTests => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTasks.DotNetTest(s => s
                .SetProjectFile(TestsDirectory / "Harbor.Architecture.Tests" / "Harbor.Architecture.Tests.csproj")
                .SetConfiguration(ConfigurationString)
                .EnableNoRestore()
                .EnableNoBuild());
        });

    /// <summary>
    ///     Publish the CLI in <b>minimal</b> mode: Plain TUI only, Ollama only,
    ///     no scripting, no plugin runtime, no alternative TUI renderers.
    ///     Produces a much smaller publish (~30 MB target vs ~109 MB full).
    /// </summary>
    Target PublishCliMinimal => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTasks.DotNetPublish(s => s
                .SetProject(AppsDirectory / "Harbor.App.Cli" / "Harbor.App.Cli.csproj")
                .SetConfiguration(ConfigurationString)
                .SetProperty("HARBOR_MINIMAL", "true")
                .SetOutput(ArtifactsDirectory / "cli-minimal"));
        });

    /// <summary>
    ///     Publish the CLI in <b>full</b> mode (default): all TUIs, all
    ///     providers, scripting, plugin runtime. ~109 MB publish.
    /// </summary>
    Target PublishCliFull => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTasks.DotNetPublish(s => s
                .SetProject(AppsDirectory / "Harbor.App.Cli" / "Harbor.App.Cli.csproj")
                .SetConfiguration(ConfigurationString)
                .SetOutput(ArtifactsDirectory / "cli-full"));
        });

    /// <summary>
    ///     Experimental native AOT publish. Linux x64 only — Spectre.Console
    ///     reflection heavy paths still cause trimming warnings.
    /// </summary>
    Target PublishAot => _ => _
        .DependsOn(Compile)
        .OnlyWhenStatic(() => EnvironmentInfo.IsLinux)
        .Executes(() =>
        {
            DotNetTasks.DotNetPublish(s => s
                .SetProject(AppsDirectory / "Harbor.App.Cli" / "Harbor.App.Cli.csproj")
                .SetConfiguration(ConfigurationString)
                .SetRuntime("linux-x64")
                .SetProperty("PublishAot", "true"));
        });

    /// <summary>
    ///     Self-contained single-file publish. ~85 MB on disk; single binary,
    ///     no .NET runtime install required on the target machine.
    /// </summary>
    Target PublishSingleFile => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTasks.DotNetPublish(s => s
                .SetProject(AppsDirectory / "Harbor.App.Cli" / "Harbor.App.Cli.csproj")
                .SetConfiguration(ConfigurationString)
                .SetRuntime("linux-x64")
                .SetSelfContained(true)
                .SetProperty("PublishSingleFile", "true")
                .SetProperty("IncludeNativeLibrariesForSelfExtract", "true")
                .SetOutput(ArtifactsDirectory / "cli-singlefile"));
        });

    Target PublishAvalonia => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTasks.DotNetPublish(s => s
                .SetProject(AppsDirectory / "Harbor.App.Avalonia" / "Harbor.App.Avalonia.csproj")
                .SetConfiguration(ConfigurationString)
                .SetOutput(ArtifactsDirectory / "avalonia"));
        });

    Target PublishBlazor => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTasks.DotNetPublish(s => s
                .SetProject(AppsDirectory / "Harbor.App.Blazor" / "Harbor.App.Blazor.csproj")
                .SetConfiguration(ConfigurationString)
                .SetOutput(ArtifactsDirectory / "blazor"));
        });

    Target PublishAll => _ => _
        .DependsOn(PublishCliMinimal, PublishCliFull, PublishSingleFile, PublishAvalonia, PublishBlazor)
        .Executes(() => { });
}
