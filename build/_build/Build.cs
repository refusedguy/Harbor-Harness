using Harbor.Build.Components;
using Harbor.Build.Configuration;
using Harbor.Build.Targets;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
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
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    // ── Solution / paths ────────────────────────────────────────────────────
    [Solution("Harbor.slnx")] readonly Solution Solution;

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    ArtifactPathResolver Resolver => new(RootDirectory, ArtifactsDirectory);

    // ── Build settings parameters ───────────────────────────────────────────
    [Parameter("Configuration: Debug or Release")] readonly BuildConfiguration Configuration = BuildConfiguration.Release;
    [Parameter("Target framework (default net10.0)")] readonly string TargetFramework = "net10.0";
    [Parameter("Runtime identifier (default linux-x64)")] readonly string Runtime = "linux-x64";

    BuildSettings Settings => new()
    {
        Configuration = Configuration,
        TargetFramework = TargetFramework,
        Runtime = Runtime
    };

    // ── Feature flag parameters ─────────────────────────────────────────────
    [Parameter("Include plugin runtime (Roslyn) — not AOT-compatible")]
    readonly bool WithPlugins = true;
    [Parameter("Include scripting (Jint JS engine) — not AOT-compatible")]
    readonly bool WithScripting = true;
    [Parameter("Include Spectre.TUI interactive renderer — not AOT-compatible")]
    readonly bool WithSpectreTui = true;
    [Parameter("Include all 4 LLM providers (false = Ollama only)")]
    readonly bool WithAllProviders = true;
    [Parameter("Include all 14 builtin tools (false = 6 core tools only)")]
    readonly bool WithAllTools = true;
    [Parameter("Minimal build — shorthand for all of the above = false")]
    readonly bool Minimal = false;

    FeatureFlags Flags => new()
    {
        WithPlugins = WithPlugins,
        WithScripting = WithScripting,
        WithSpectreTui = WithSpectreTui,
        WithAllProviders = WithAllProviders,
        WithAllTools = WithAllTools,
        Minimal = Minimal
    };

    // ── Publish / Release parameters ────────────────────────────────────────
    [Parameter("Publish variant (FrameworkDependent, SelfContained, SingleFile, SingleFileSelfContained, Trimmed, AOT)")]
    readonly PublishVariant Variant = PublishVariant.FrameworkDependent;
    [Parameter("Archive format (None, TarGz, Zip)")]
    readonly ArchiveFormat Archive = ArchiveFormat.None;
    [Parameter("Release tag (e.g. v0.7.0) for the Release target")]
    readonly string ReleaseTag = string.Empty;
    [Parameter("GitHub repo (owner/name) for the Release target")]
    readonly string ReleaseRepo = "harbor-sh/harbor";
    [Parameter("App name to publish (default Harbor.App.Cli)")]
    readonly string AppName = "Harbor.App.Cli";

    // Lazily-constructed components (singletons for this build run)
    CliBuildConfigurator Configurator => new();
    PublishVariantBuilder VariantBuilder => new(Settings, Configurator);
    ArchiveBuilder Archiver => new();
    GitHubReleaseUploader Uploader => new();

    // ── Targets ─────────────────────────────────────────────────────────────
    Target Clean => _ => _.Before(Restore)
        .Executes(() => CleanTarget.Execute(Resolver));

    Target Restore => _ => _
        .Executes(() => RestoreTarget.Execute(Solution));

    Target Compile => _ => _.DependsOn(Restore)
        .Executes(() => CompileTarget.Execute(Solution, Settings));

    Target Test => _ => _.DependsOn(Compile)
        .Executes(() => TestTarget.Execute(Solution, Settings));

    Target ArchitectureTests => _ => _.DependsOn(Compile)
        .Executes(() => ArchitectureTestTarget.Execute(Resolver, Settings));

    Target Publish => _ => _.DependsOn(Compile)
        .Executes(() => PublishTarget.Execute(Resolver, VariantBuilder, AppName, Variant, Flags));

    Target PublishArchive => _ => _.DependsOn(Compile)
        .Executes(() => AllTargets.PublishAndArchive(
            Resolver, VariantBuilder, Archiver,
            AppName, Variant, Flags, Settings, Archive));

    Target Release => _ => _.DependsOn(Compile)
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
    Target PublishIpcServer => _ => _.DependsOn(Compile)
        .Executes(() => IpcPublishTarget.ExecuteIpcServer(Resolver, Settings, Flags));

    /// <summary>
    ///     Publish the <c>ipc-client</c> variant of the CLI. The resulting
    ///     binary is a thin client that connects to a running
    ///     <see cref="PublishIpcServer"/> via MessagePack-over-pipe.
    ///     Output: <c>artifacts/ipc-client/</c>.
    /// </summary>
    Target PublishIpcClient => _ => _.DependsOn(Compile)
        .Executes(() => IpcPublishTarget.ExecuteIpcClient(Resolver, Settings, Flags));
}
