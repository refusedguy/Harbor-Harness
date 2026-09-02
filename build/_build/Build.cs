using System.Diagnostics;
using Harbor.Build.Components;
using Harbor.Build.Meta;
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
///         <c>Targets/</c>. Each target lives in its own file (SRP).
///     </para>
///     <para>
///         <b>Targets</b> (run via <c>./build.sh &lt;Target&gt;</c>): Clean,
///         Restore, Compile (default), Test, ArchitectureTests, Publish,
///         PublishArchive, Release, PublishIpcServer, PublishIpcClient — plus
///         the meta commands List, Help, Doctor, What (machine-readable
///         catalog, environment checks, change-impact mapping).
///     </para>
///     <para>
///         <b>Agent interface</b>: never invent build commands — ask the
///         build itself:
///         <c>./build.sh list|doctor|what --path X --format json [--dry-run]</c>.
///         Exit codes: 0 ok · 1 target failed · 2 usage error · 3 doctor
///         found failures · 4 what --strict low confidence.
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

    // ── Meta command parameters ─────────────────────────────────────────────
    [Parameter("Output format: Pretty (human) or Json (JSON-lines on stdout)")]
    private readonly OutputFormat Format = OutputFormat.Pretty;
    [Parameter("Show the plan (argv + planned artifacts) without executing")]
    private readonly bool DryRun;
    [Parameter("Duplicate the machine-readable stream to this file")]
    private readonly string? Out;
    [Parameter("Path for the 'what' command (file or directory)")]
    private readonly string? Path;
    [Parameter("Exit 4 when 'what' confidence is low")]
    private readonly bool Strict;
    [Parameter("Include Publish commands in the 'what' plan for app changes")]
    private readonly bool IncludePublish;
    [Parameter("Comma-separated doctor check ids to run (default: all)")]
    private readonly string? Check;

    private TextWriter? _realStdout;
    private BuildOutput? _output;
    private readonly HashSet<string> _invokedTargets =
        TargetCatalog.ParseInvokedTargetNames(Environment.GetCommandLineArgs());

    private ArtifactPathResolver Resolver => new(RootDirectory, ArtifactsDirectory);
    private AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    /// <summary>
    ///     Emitter bound to the captured real stdout (machine channel in Json
    ///     mode) — constructed lazily, after NUKE has injected parameters.
    /// </summary>
    private BuildOutput Output => _output ??= BuildOutput.Create(Format, DryRun, _realStdout ?? Console.Out, Out);

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

    // ── Lifecycle hooks: stream routing + final run_end ─────────────────────
    protected override void OnBuildCreated()
    {
        // Capture the real stdout before anything can re-point Console.Out.
        _realStdout = Console.Out;
        base.OnBuildCreated();
    }

    protected override void OnBuildInitialized()
    {
        if (Format == OutputFormat.Json)
        {
            // Machine lines flow through the captured stdout; everything else
            // (NUKE/msbuild/human noise) goes to stderr from here on.
            Console.SetOut(Console.Error);
            NoLogo = true;
            Verbosity = Nuke.Common.Verbosity.Minimal;
        }
        base.OnBuildInitialized();
    }

    protected override void OnBuildFinished()
    {
        try
        {
            var output = Output;
            var failed = output.FailedTargets;
            var status = DryRun && failed.Count == 0 ? "planned"
                : failed.Count > 0 ? "failed"
                : "success";
            var exitCode = ExitCode ?? (failed.Count > 0 ? 1 : 0);
            output.RunEnd(status, failed, exitCode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"run_end emission failed: {ex.Message}");
        }
        base.OnBuildFinished();
    }

    // ── Target wrapper: events + failure bookkeeping ────────────────────────
    private void Run(string name, Action body)
    {
        var output = Output;
        output.TargetStart(name);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            body();
            output.TargetEnd(name, TargetStatus(name), stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            output.MarkFailed(name);
            output.TargetEnd(name, "failed", stopwatch.ElapsedMilliseconds);
            output.Error(name, ex.Message);
            throw;
        }
    }

    private async Task RunAsync(string name, Func<Task> body)
    {
        var output = Output;
        output.TargetStart(name);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await body();
            output.TargetEnd(name, TargetStatus(name), stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            output.MarkFailed(name);
            output.TargetEnd(name, "failed", stopwatch.ElapsedMilliseconds);
            output.Error(name, ex.Message);
            throw;
        }
    }

    private string TargetStatus(string name) => DryRun
        ? _invokedTargets.Contains(name) ? "planned" : "skipped-dryrun"
        : "success";

    // ── Targets ─────────────────────────────────────────────────────────────
    private Target Clean => _ => _.Before(Restore)
        .Executes(() => Run("Clean", () => CleanTarget.Execute(Resolver, Output)));

    private Target Restore => _ => _
        .Executes(() => Run("Restore", () => RestoreTarget.Execute(Solution, Output)));

    private Target Compile => _ => _.DependsOn(Restore)
        .Executes(() => Run("Compile", () => CompileTarget.Execute(Solution, Settings, Output)));

    private Target Test => _ => _.DependsOn(Compile)
        .Executes(() => Run("Test", () => TestTarget.Execute(Solution, Settings, Output)));

    private Target ArchitectureTests => _ => _.DependsOn(Compile)
        .Executes(() => Run("ArchitectureTests", () =>
            ArchitectureTestTarget.Execute(Resolver, Settings, Output)));

    private Target Publish => _ => _.DependsOn(Compile)
        .Executes(() => Run("Publish", () =>
            PublishTarget.Execute(Resolver, VariantBuilder, AppName, Variant, Flags, Output)));

    private Target PublishArchive => _ => _.DependsOn(Compile)
        .Executes(() => Run("PublishArchive", () =>
            AllTargets.PublishAndArchive(
                Resolver, VariantBuilder, Archiver,
                AppName, Variant, Flags, Settings, Archive, Output)));

    private Target Release => _ => _.DependsOn(Compile)
        .Executes(() => RunAsync("Release", async () =>
        {
            var variants = ReleaseTarget.DefaultReleaseVariants(Flags);
            await ReleaseTarget.ExecuteAsync(
                Resolver, VariantBuilder, Archiver, Uploader,
                AppName, variants, Flags, Settings, ReleaseTag, ReleaseRepo, Output);
        }));

    /// <summary>
    ///     Publish the <c>ipc-server</c> variant of the CLI. The resulting
    ///     binary hosts the AgentLoop + registries and exposes them via
    ///     MessagePack-over-pipe. Output: <c>artifacts/ipc-server/</c>.
    /// </summary>
    private Target PublishIpcServer => _ => _.DependsOn(Compile)
        .Executes(() => Run("PublishIpcServer", () =>
            IpcPublishTarget.ExecuteIpcServer(Resolver, Settings, Flags, Output)));

    /// <summary>
    ///     Publish the <c>ipc-client</c> variant of the CLI. The resulting
    ///     binary is a thin client that connects to a running
    ///     <see cref="PublishIpcServer" /> via MessagePack-over-pipe.
    ///     Output: <c>artifacts/ipc-client/</c>.
    /// </summary>
    private Target PublishIpcClient => _ => _.DependsOn(Compile)
        .Executes(() => Run("PublishIpcClient", () =>
            IpcPublishTarget.ExecuteIpcClient(Resolver, Settings, Flags, Output)));

    // ── Meta commands ───────────────────────────────────────────────────────
    /// <summary>Catalog of targets/parameters/flags; verified against reflection each run.</summary>
    private Target List => _ => _
        .Executes(() => Run("List", () =>
            Output.EmitDocument(
                TargetCatalog.BuildListDocumentJson(this),
                () => TargetCatalog.RenderPretty(Output))));

    /// <summary>Human-readable alias for List — pretty content, stderr in Json mode.</summary>
    private new Target Help => _ => _
        .Executes(() => Run("Help", () => TargetCatalog.RenderPretty(Output)));

    /// <summary>Offline environment checks; exit 3 when any check fails.</summary>
    private Target Doctor => _ => _
        .Executes(() => Run("Doctor", () =>
        {
            var filter = ParseCheckFilter();
            if (filter is { } parsedFilter && parsedFilter.Unknown.Count > 0)
            {
                Output.Error("Doctor",
                    $"unknown check id(s): {string.Join(", ", parsedFilter.Unknown)}. " +
                    $"Known: {string.Join(", ", DoctorChecks.AllCheckIds)}");
                ExitCode = 2;
                return;
            }
            var context = new DoctorContext(
                RootDirectory.ToString(),
                TargetFramework,
                Variant is PublishVariant.AOT or PublishVariant.Trimmed,
                _invokedTargets.Contains("Release"),
                ReleaseTag);
            var report = DoctorChecks.RunAll(context, filter?.Ids);
            Output.EmitDocument(
                DoctorChecks.ToJson(report),
                () => DoctorChecks.RenderPretty(report, Output));
            if (report.ExitCode != 0)
            {
                ExitCode = report.ExitCode;
            }
        }));

    /// <summary>Path → affected projects → minimal command plan; exit 4 on strict+low.</summary>
    private Target What => _ => _
        .Executes(() => Run("What", () =>
        {
            ImpactMapperSelfTest.RunAll(Output);
            if (string.IsNullOrWhiteSpace(Path))
            {
                Output.Error("What", "missing --path <file-or-directory>");
                ExitCode = 2;
                return;
            }
            var input = new ImpactInput(RootDirectory.ToString(), CollectProjects());
            var answer = ImpactMapper.Analyze(input, Path, new ImpactOptions(Strict, IncludePublish));
            Output.EmitDocument(
                ImpactMapper.ToJson(answer),
                () => ImpactMapper.RenderPretty(answer, Output));
            if (Strict && answer.Confidence == ImpactAnswer.Low)
            {
                ExitCode = 4;
            }
        }));

    private (HashSet<string>? Ids, List<string> Unknown)? ParseCheckFilter()
    {
        if (string.IsNullOrWhiteSpace(Check))
        {
            return null;
        }
        var ids = Check.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = ids
            .Where(id => !DoctorChecks.AllCheckIds.Contains(id, StringComparer.Ordinal))
            .ToList();
        return (ids.ToHashSet(StringComparer.Ordinal), unknown);
    }

    /// <summary>
    ///     Projects visible to the mapper: solution members plus any csproj on
    ///     disk under src/, apps/, tests/ (e.g. apps not registered in the
    ///     slnx yet). Deduplicated by path; bin/obj ignored.
    /// </summary>
    private List<ImpactProject> CollectProjects()
    {
        var byPath = new Dictionary<string, ImpactProject>(StringComparer.Ordinal);
        foreach (var project in Solution.AllProjects)
        {
            byPath[project.Path.ToString()] = new ImpactProject(project.Name, project.Path.ToString());
        }
        foreach (var relativeRoot in new[] { "src", "apps", "tests" })
        {
            var absoluteRoot = RootDirectory / relativeRoot;
            if (!Directory.Exists(absoluteRoot))
            {
                continue;
            }
            foreach (var csproj in Directory.EnumerateFiles(absoluteRoot, "*.csproj", SearchOption.AllDirectories))
            {
                var normalized = csproj.Replace('\\', '/');
                if (normalized.Contains("/bin/", StringComparison.Ordinal) ||
                    normalized.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }
                var name = System.IO.Path.GetFileNameWithoutExtension(csproj);
                byPath.TryAdd(normalized, new ImpactProject(name, normalized));
            }
        }
        return byPath.Values.ToList();
    }

    public static int Main() => Execute<Build>(x => x.Compile);
}
