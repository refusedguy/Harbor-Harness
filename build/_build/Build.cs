using Harbor.Build.Components;
using Harbor.Build.Targets;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
namespace Harbor.Build;
/// <summary>
///     NUKE build entry point for the Harbor solution.
/// </summary>
/// <remarks>
///     <para>
///         <b>Composition root only</b> — this class wires up [Parameter]s and
///         delegates to the static <c>*Target</c> classes in
///         <c>Targets/</c>. Each target lives in its own file (SRP); this file
///         is intentionally &lt;100 lines.
///     </para>
///     <para>
///         <b>Targets</b> (run via <c>./build.sh &lt;Target&gt;</c>):
///     </para>
///     <list type="bullet">
///         <item><c>Clean</c> — delete bin/obj + artifacts.</item>
///         <item><c>Restore</c> — dotnet restore.</item>
///         <item><c>Compile</c> (default) — dotnet build in Release.</item>
///         <item><c>Test</c> — dotnet test --no-build.</item>
///         <item><c>ArchitectureTests</c> — run only Harbor.Architecture.Tests.</item>
///         <item><c>Publish</c> — publish CLI with --variant + --minimal flags.</item>
///         <item><c>PublishArchive</c> — Publish + tar.gz/zip the output.</item>
///         <item><c>Release</c> — publish all variants, archive, upload to GitHub.</item>
///     </list>
///     <para>
///         See <c>build/README.md</c> for examples and <c>docs/BUILD_SYSTEM.md</c>
///         for the full guide.
///     </para>
/// </remarks>
internal class Build : NukeBuild
{
    [Parameter("App name to publish (default Harbor.App.Cli)")] private readonly string AppName = "Harbor.App.Cli";
    [Parameter("Archive format (None, TarGz, Zip)")] private readonly ArchiveFormat Archive = ArchiveFormat.None;

    // ── Build settings parameters ───────────────────────────────────────────
    [Parameter("Configuration: Debug or Release")] private readonly BuildConfiguration Configuration = BuildConfiguration.Release;
    [Parameter("Minimal build — shorthand for all of the above = false")] private readonly bool Minimal;
    [Parameter("GitHub repo (owner/name) for the Release target")] private readonly string ReleaseRepo = "harbor-sh/harbor";
    [Parameter("Release tag (e.g. v0.7.0) for the Release target")] private readonly string ReleaseTag = string.Empty;
    [Parameter("Runtime identifier (default linux-x64)")] private readonly string Runtime = "linux-x64";

    // ── Solution / paths ────────────────────────────────────────────────────
    [Solution("Harbor.slnx")] private readonly Solution Solution;
    [Parameter("Target framework (default net10.0)")] private readonly string TargetFramework = "net10.0";

    // ── Publish / Release parameters ────────────────────────────────────────
    [Parameter("Publish variant (FrameworkDependent, SelfContained, SingleFile, SingleFileSelfContained, Trimmed, AOT)")]
    private readonly PublishVariant Variant = PublishVariant.FrameworkDependent;
    [Parameter("Include all 4 LLM providers (false = Ollama only)")] private readonly bool WithAllProviders = true;
    [Parameter("Include all 14 builtin tools (false = 6 core tools only)")]
    private readonly bool WithAllTools = true;

    // ── Feature flag parameters ─────────────────────────────────────────────
    [Parameter("Include plugin runtime (Roslyn) — not AOT-compatible")] private readonly bool WithPlugins = true;
    [Parameter("Include scripting (Jint JS engine) — not AOT-compatible")] private readonly bool WithScripting = true;
    [Parameter("Include Spectre.TUI interactive renderer — not AOT-compatible")]
    private readonly bool WithSpectreTui = true;

    private AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    private ArtifactPathResolver Resolver => new(RootDirectory, ArtifactsDirectory);

    private BuildSettings Settings => new()
    {
        Configuration = Configuration,
        TargetFramework = TargetFramework,
        Runtime = Runtime
    };

    private FeatureFlags Flags => new()
    {
        WithPlugins = WithPlugins,
        WithScripting = WithScripting,
        WithSpectreTui = WithSpectreTui,
        WithAllProviders = WithAllProviders,
        WithAllTools = WithAllTools,
        Minimal = Minimal
    };

    // Lazily-constructed components (singletons for this build run)
    private CliBuildConfigurator Configurator => new();
    private PublishVariantBuilder VariantBuilder => new(Settings, Configurator);
    private ArchiveBuilder Archiver => new();
    private GitHubReleaseUploader Uploader => new();

    // ── Targets ─────────────────────────────────────────────────────────────
    private Target Clean => _ => _.Before(Restore)
        .Executes(() => CleanTarget.Execute(Resolver));

    private Target Restore => _ => _
        .Executes(() => RestoreTarget.Execute(Solution));

    private Target Compile => _ => _.DependsOn(Restore)
        .Executes(() => CompileTarget.Execute(Solution, Settings));

    private Target Test => _ => _.DependsOn(Compile)
        .Executes(() => TestTarget.Execute(Solution, Settings));

    private Target ArchitectureTests => _ => _.DependsOn(Compile)
        .Executes(() => ArchitectureTestTarget.Execute(Resolver, Settings));

    private Target Publish => _ => _.DependsOn(Compile)
        .Executes(() => PublishTarget.Execute(Resolver, VariantBuilder, AppName, Variant, Flags));

    private Target PublishArchive => _ => _.DependsOn(Compile)
        .Executes(() => AllTargets.PublishAndArchive(
            Resolver, VariantBuilder, Archiver,
            AppName, Variant, Flags, Settings, Archive));

    private Target Release => _ => _.DependsOn(Compile)
        .Executes(async () =>
        {
            var variants = ReleaseTarget.DefaultReleaseVariants(Flags);
            await ReleaseTarget.ExecuteAsync(
                Resolver, VariantBuilder, Archiver, Uploader,
                AppName, variants, Flags, Settings, ReleaseTag, ReleaseRepo);
        });

    /// <summary>
    ///     Publish the <c>ipc-server</c> variant of the CLI. The resulting
    ///     binary hosts the AgentLoop + registries and exposes them via
    ///     MessagePack-over-pipe. Output: <c>artifacts/ipc-server/</c>.
    /// </summary>
    private Target PublishIpcServer => _ => _.DependsOn(Compile)
        .Executes(() => IpcPublishTarget.ExecuteIpcServer(Resolver, Settings, Flags));

    /// <summary>
    ///     Publish the <c>ipc-client</c> variant of the CLI. The resulting
    ///     binary is a thin client that connects to a running
    ///     <see cref="PublishIpcServer" /> via MessagePack-over-pipe.
    ///     Output: <c>artifacts/ipc-client/</c>.
    /// </summary>
    private Target PublishIpcClient => _ => _.DependsOn(Compile)
        .Executes(() => IpcPublishTarget.ExecuteIpcClient(Resolver, Settings, Flags));
    public static int Main() => Execute<Build>(x => x.Compile);
}
