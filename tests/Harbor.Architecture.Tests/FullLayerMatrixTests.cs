// FullLayerMatrixTests.cs — table-driven layer rules for EVERY main-solution
// Harbor src assembly (ROP-D Z2).
//
// Before this file existed, ~28 of the ~46 src projects had zero architecture
// rules: only the assemblies that happened to have a typeof() probe somewhere
// in LayerDependencyTests/NetArchLayerRules were covered. This matrix closes
// the gap with one data table plus two derived checks:
//
//   1. Per-assembly: actual Assembly.GetReferencedAssemblies() ⊆ Allowed set.
//   2. Table-level: the Allowed sets themselves must respect the layer rules
//      (Presentation ↛ Infrastructure/Application, Infrastructure ↛
//      Presentation, Domain ↛ nothing but Domain) except entries listed in
//      DocumentedExceptions with a reason.
//
// A new src project therefore needs BOTH a ProjectReference in the .csproj and
// a row here — otherwise AllSrcAssembliesAreCovered fails loudly. See
// docs/ARCHITECTURE_LAYERS.md §2 for the canonical matrix this encodes.
//
// Out of scope by design — see OutOfScopeAssemblies for the authoritative list
// with a reason per entry. EnforcerIntegrityTests.SrcProjects_AreAllClassified
// fails when a src project is in neither this table nor that list, so the
// "out of scope" set can no longer grow silently (the #450 regression class).

namespace Harbor.Architecture.Tests;

public class FullLayerMatrixTests
{
    /// <summary>Layers a src assembly can belong to.</summary>
    internal enum Layer
    {
        /// <summary>Pure contracts / BCL-only helpers. Bottom of the pyramid.</summary>
        Domain,
        /// <summary>UI framework family + concrete renderers (Tui.*, Desktop.*, Ui.Framework.*).</summary>
        Presentation,
        /// <summary>Use-case orchestration: Application, Registries, plugin contract/runtime surface.</summary>
        Application,
        /// <summary>Implementations: providers, storage, tools, IPC endpoints, telemetry, plugin machinery.</summary>
        Infrastructure,
        /// <summary>DI wiring over everything (Harbor.Hosting). Unrestricted.</summary>
        CompositionRoot,
    }

    internal sealed record Row(Layer Layer, string[] Allowed);

    /// <summary>
    ///     src projects deliberately NOT on the matrix, each with the reason. Every
    ///     entry must have a non-empty reason —
    ///     <c>EnforcerIntegrityTests.SrcProjects_AreAllClassified</c> enforces it.
    /// </summary>
    internal static readonly Dictionary<string, string> OutOfScopeAssemblies = new(StringComparer.Ordinal)
    {
        // Roslyn source-generator project (referenced with
        // OutputItemType=Analyzer by the projects that need generated code). It
        // produces no runtime assembly, so there is no layer edge to constrain.
        ["Harbor.CodeGen"] = "Source-generator project; consumed via OutputItemType=Analyzer, emits no runtime assembly.",

        // Harbor.Plugins.Host.csproj produces the assembly 'harbor-plugins-host'
        // (OutputType=Exe): an out-of-process MCP stdio server, i.e. an
        // app/composition root like apps/*. It may reference anything.
        ["Harbor.Plugins.Host"] = "OutputType=Exe out-of-process MCP stdio server (assembly 'harbor-plugins-host') — a composition root.",
    };

    /// <summary>
    ///     <c>src/</c> folders that hold shared source but produce no assembly —
    ///     their files are <c>&lt;Compile Include&gt;</c>-linked into several
    ///     projects, so there is nothing to put on the matrix. Tracked here so the
    ///     "no csproj" case is an explicit, checked statement rather than an
    ///     unlisted directory.
    /// </summary>
    internal static readonly string[] SharedSourceFolders = ["Harbor.Providers.Shared"];

    /// <summary>
    ///     Projects referenced as source generators (OutputItemType=Analyzer).
    ///     A generator is a build-time dependency, not a layer edge, so it is
    ///     excluded from the matrix — but it must be declared here, so that a
    ///     NEW analyzer reference cannot slip past unnoticed.
    /// </summary>
    internal static readonly string[] SourceGeneratorProjects = ["Harbor.CodeGen"];

    // The single source of truth for "which src assemblies exist AND are under
    // enforcement". LayerDependencyTests.AllExpectedHarborAssembliesAreLoaded
    // consumes this list too.
    public static readonly string[] AllSrcAssemblies =
    [
        // Domain
        "Harbor.Abstractions.Contracts",
        "Harbor.Diagnostics.Abstractions",
        "Harbor.Extensions",
        "Harbor.Abstractions",
        "Harbor.Ipc.Abstractions",
        "Harbor.Ui.Framework.Abstractions",
        // Presentation
        "Harbor.Terminal.Abstractions",
        "Harbor.Ui.Framework",
        "Harbor.Ui.Framework.State",
        "Harbor.Ui.Framework.Reducers",
        "Harbor.Ui.Framework.Services",
        "Harbor.Ui.Framework.ViewModels",
        "Harbor.Ui.Framework.Projection",
        "Harbor.Ui.Framework.Rendering",
        "Harbor.DesignSystem",
        "Harbor.Ui.Framework.Sessions",
        "Harbor.Desktop.Abstractions",
        "Harbor.Desktop.Shared",
        "Harbor.Desktop.Animations",
        "Harbor.Tui.Notifications",
        "Harbor.Tui.AnsiPlain",
        "Harbor.Tui.CellForge.Engine",
        "Harbor.Tui.CellForge",
        // #450: was reachable from Harbor.Hosting but present in NEITHER this
        // inventory, the matrix, nor an out-of-scope list — i.e. its own
        // dependencies were unbounded. SharpConsoleUI is a third-party renderer
        // library (external/ConsoleEx), referenced only for the renderer it wraps.
        "Harbor.Tui.NickConsoleEx",
        // Application
        "Harbor.Application",
        "Harbor.Registries",
        "Harbor.Plugins.Abstractions",
        "Harbor.Plugins.Runtime",
        // Infrastructure
        "Harbor.Providers.OpenAiCompatible",
        "Harbor.Providers.Anthropic",
        "Harbor.Providers.OpenAI",
        "Harbor.Providers.Ollama",
        "Harbor.Storage.Jsonl",
        "Harbor.Storage.Memory",
        "Harbor.Storage.Sqlite",
        "Harbor.Tools.Builtin",
        "Harbor.Lsp",
        "Harbor.Terminal.Pty",
        "Harbor.Logging",
        "Harbor.Transport.Remote",
        "Harbor.Telemetry.Core",
        "Harbor.Telemetry.Otlp",
        "Harbor.Ipc.Client",
        "Harbor.Ipc.InProcess",
        "Harbor.Ipc.Server",
        "Harbor.Plugins.Storage",
        "Harbor.Plugins.Compilation",
        "Harbor.Plugins.Instantiation",
        "Harbor.Plugins.Registration",
        "Harbor.Plugins.Hosting",
        // CompositionRoot
        "Harbor.Hosting",
    ];

    /// <summary>
    ///     One (from → to) edge that violates the naive layer rules. Unlike a
    ///     project-granular allow, an exception is <b>file-scoped</b>: only the
    ///     source files listed in <see cref="Sites" /> may bind a type from the
    ///     target assembly. A new file reaching into the target assembly fails
    ///     <c>EnforcerIntegrityTests.DocumentedExceptions_AreScopedToNamedFiles</c>,
    ///     so an exception can no longer hide future violations.
    /// </summary>
    internal sealed record DocumentedException(string Target, string Reason, string[] Sites);

    // (from → to) edges that violate the naive layer rules but are accepted,
    // each with its reason. Anything NOT listed here is a hard failure.
    internal static readonly Dictionary<string, DocumentedException[]> DocumentedExceptions = new()
    {
        // #188 (part of #96) Presentation → Application tech debt, owner: architecture:
        // ProviderModelPickerViewModel / OnboardingViewModel consume ProviderPresets
        // from Harbor.Application.Configuration. The Harbor.Core facade that used to
        // carry this edge was removed in #188 and the facade itself deleted in #451.
        // Future fix: move the preset catalog to Domain (Harbor.Abstractions.Providers)
        // — tracked per site, so the debt cannot spread past these two files.
        ["Harbor.Desktop.Abstractions"] =
        [
            new DocumentedException(
                "Harbor.Application",
                "#188/#96 Presentation→Application debt: ProviderPresets catalog. Fix: move presets to Domain (Harbor.Abstractions.Providers).",
                [
                    "ViewModels/OnboardingViewModel.cs",
                    "ViewModels/ProviderModelPickerViewModel.cs",
                ]),
        ],
        // ITuiPlugin / TUI vocabulary lives in Terminal.Abstractions by design;
        // plugin-surface assemblies legitimately reach Presentation for it.
        ["Harbor.Plugins.Abstractions"] =
        [
            new DocumentedException(
                "Harbor.Terminal.Abstractions",
                "ITuiPlugin is TUI vocabulary; the plugin contract surface must name it.",
                ["IPluginLoadHost.cs"]),
            new DocumentedException(
                "Harbor.Ui.Framework.State",
                "Plugin manifests describe TUI state projections (panel payloads).",
                ["IPluginLoadHost.cs"]),
        ],
        ["Harbor.Plugins.Compilation"] =
        [
            new DocumentedException(
                "Harbor.Terminal.Abstractions",
                "Compile-time reference passing builds ITuiPlugin vocabulary for the plugin.",
                ["PluginAssemblyReferences.cs"]),
        ],
        ["Harbor.Plugins.Registration"] =
        [
            new DocumentedException(
                "Harbor.Terminal.Abstractions",
                "ITuiPlugin is TUI vocabulary; the registrar registers TUI plugins.",
                ["PluginRegistrar.cs"]),
            new DocumentedException(
                "Harbor.Ui.Framework.State",
                "Registered TUI plugins carry view-model/state payloads.",
                ["PanelRegistryPluginAdapter.cs", "PluginRegistrar.cs"]),
        ],
    };

    internal static readonly Dictionary<string, Row> Matrix = new()
    {
        // ---- Domain -------------------------------------------------------
        ["Harbor.Abstractions.Contracts"] = new(Layer.Domain, []),
        ["Harbor.Diagnostics.Abstractions"] = new(Layer.Domain, []),
        ["Harbor.Extensions"] = new(Layer.Domain, []),
        ["Harbor.Abstractions"] = new(Layer.Domain, ["Harbor.Abstractions.Contracts"]),
        ["Harbor.Ipc.Abstractions"] = new(Layer.Domain, ["Harbor.Abstractions"]),
        // The IL *does* reference Harbor.Abstractions here even though the
        // assembly binds no type from it by hand: the generated AssemblyInfo
        // attributes (InternalsVisibleTo targets) force the reference. The
        // liveness rule would otherwise call this a stale entry.
        ["Harbor.Ui.Framework.Abstractions"] = new(Layer.Domain, ["Harbor.Abstractions"]),

        // ---- Presentation -------------------------------------------------
        // #450: "Harbor.Ui.Framework" was permitted here but never used. The
        // project is an empty meta-package shell (no .cs files at all), so no
        // consumer can bind a type from it and Roslyn never emits the
        // AssemblyRef — the edge was unenforceable permission. Removed; the leaf
        // Ui.Framework.* modules are the real dependencies.
        ["Harbor.Terminal.Abstractions"] = new(Layer.Presentation, ["Harbor.Abstractions"]),
        ["Harbor.Ui.Framework"] = new(Layer.Presentation,
        [
            "Harbor.Ui.Framework.Abstractions", "Harbor.Ui.Framework.State",
            "Harbor.Ui.Framework.Services", "Harbor.Ui.Framework.ViewModels",
            "Harbor.Ui.Framework.Projection", "Harbor.Ui.Framework.Sessions",
        ]),
        ["Harbor.Ui.Framework.State"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.Abstractions",
                // #33 T1: KeyEventAdapter consumes the BCL-only UiKeyDto key
                // vocabulary (Rendering.Input) — the reversed edge replacing the
                // old Rendering→State one; Presentation→Presentation conforms.
                "Harbor.Ui.Framework.Rendering"]),
        ["Harbor.Ui.Framework.Reducers"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.State"]),
        ["Harbor.Ui.Framework.Services"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Reducers", "Harbor.Ui.Framework.Abstractions"]),
        ["Harbor.Ui.Framework.ViewModels"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Services", "Harbor.Ui.Framework.Abstractions"]),
        ["Harbor.Ui.Framework.Projection"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Abstractions",
             // RgbColor is defined in the standalone DesignSystem package but
             // keeps its historical Projection namespace for compatibility.
             "Harbor.DesignSystem"]),
        // Renderer-agnostic shared layer: cell/screen primitives, input
        // vocabulary and chat widgets consumed by every renderer backend.
        // Leaf Presentation library over the HDS token catalog (DesignSystem)
        // and motion tokens (Desktop.Animations) backing ChatPalette/PanelFx
        // (ChatPalette + the cell-style primitives physically live in the
        // DesignSystem package assembly now).
        // #33 T1: KeyEventMapper (Input/) translates raw keys to the BCL-only
        // UiKeyDto vocabulary (IKeyVocabulary) so every renderer shares one key
        // meaning — State consumes it via KeyEventAdapter, hence NO State edge
        // (and no Projection edge: unrealized, and State→Rendering plus
        // Rendering→Projection→State would be an MSBuild cycle).
        ["Harbor.Ui.Framework.Rendering"] = new(Layer.Presentation,
            ["Harbor.DesignSystem", "Harbor.Desktop.Animations",
                // #75: canonical ctx% helper (ContextUsage) — Presentation → Domain is rule-conforming.
                "Harbor.Abstractions.Contracts"]),
        // HDS v1 token catalog — standalone leaf: ZERO Harbor references. The
        // design-system package ships RgbColor (under the historical Projection
        // namespace) plus the cell-style primitives and ChatPalette, so
        // Projection/Rendering/CellForge/apps all resolve them from here.
        ["Harbor.DesignSystem"] = new(Layer.Presentation, []),
        ["Harbor.Ui.Framework.Sessions"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Services", "Harbor.Ui.Framework.ViewModels", "Harbor.Ui.Framework.Abstractions"]),
        // #450: "Harbor.Terminal.Abstractions" and "Harbor.Ui.Framework" were both
        // permitted but never referenced. Terminal.Abstractions is dead permission
        // (no file names the ITuiRenderer/ITuiView vocabulary); Ui.Framework is an
        // empty assembly (see the Terminal.Abstractions row). The leaf
        // Ui.Framework.* modules below are what this project actually binds.
        ["Harbor.Desktop.Abstractions"] = new(Layer.Presentation,
        [
            "Harbor.Abstractions",
            "Harbor.Ui.Framework.ViewModels",
            "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Services",
            "Harbor.Ui.Framework.Sessions",
        ]),
        ["Harbor.Desktop.Shared"] = new(Layer.Presentation,
            // #462: Commands/SlashCommands projects the shared SlashCommandCatalog
            // (Ui.Framework.Abstractions, Domain) rather than a private literal copy.
            // #450: "Harbor.Ui.Framework" removed — empty assembly, never bound.
            ["Harbor.Desktop.Abstractions", "Harbor.Ui.Framework.Abstractions"]),
        // RgbColor is defined in Harbor.DesignSystem (standalone package); the
        // token types come through that same reference.
        ["Harbor.Desktop.Animations"] = new(Layer.Presentation,
            ["Harbor.DesignSystem"]),
        ["Harbor.Tui.Notifications"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Terminal.Abstractions"]),
        // renderer-unification Phase 4: Ansi + Plain merged into one assembly;
        // styling flows through IEscapeCodeStrategy (Ansi / Null impls).
        // Issue #77: chat writes land in the DI-shared UiStore (Presentation→
        // Presentation edge, same as CellForge.Engine).
        ["Harbor.Tui.AnsiPlain"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Terminal.Abstractions", "Harbor.Ui.Framework.State"]),
        // CellForge engine (issue #33 split): input/parsing/capabilities/cell
        // primitives. No Chat vocabulary: wheel ticks surface as AppMsg via
        // the State KeyEventAdapter over the shared UiKeyDto vocabulary, cell
        // styles via DesignSystem tokens, shared blocks via Ui.Framework.Rendering.
        ["Harbor.Tui.CellForge.Engine"] = new(Layer.Presentation,
            [
                "Harbor.Abstractions",
                "Harbor.Ui.Framework.State",
                "Harbor.Ui.Framework.Rendering",
                "Harbor.DesignSystem",
            ]),
        // CellForge owns its own input+render stack; reuses Presentation-state
        // streaming buffers (StreamingSync/ChunkedBuffer) and the shared
        // renderer-agnostic layer. Terminal.Abstractions supplies the
        // ITuiRenderer/BaseTuiRenderer adapter surface (Phase 2).
        // DesignSystem supplies the HDS v1 token catalog for ChatPalette;
        // Desktop.Animations supplies the motion tokens (PanelFx); the
        // projection edge is RgbColor's AssemblyRef via that same bridge.
        // TOP-1 #27 (CF-A-001 follow-up): Services/ViewModels/Sessions (sibling
        // Presentation modules: toasts/dialogs/overlays, shared VMs, session
        // orchestration) + Ui.Framework.Abstractions (Domain contracts) are the
        // panel-integration surface for epic B-K; Presentation->Presentation and
        // Presentation->Domain edges satisfy MatrixTable_RespectsLayerRules.
        ["Harbor.Tui.CellForge"] = new(Layer.Presentation,
            [
                "Harbor.Abstractions", "Harbor.Terminal.Abstractions",
                "Harbor.Tui.CellForge.Engine",
                "Harbor.Ui.Framework.State",
                "Harbor.Ui.Framework.Projection",
                "Harbor.Ui.Framework.Rendering",
                "Harbor.Ui.Framework.Services",
                "Harbor.Ui.Framework.ViewModels",
                "Harbor.Ui.Framework.Sessions",
                "Harbor.Ui.Framework.Abstractions",
                "Harbor.DesignSystem", "Harbor.Desktop.Animations",
            ]),
        // #450: SharpConsoleUI-based renderer (HARBOR_TUI=nickconsoleex), wired
        // behind HarborWithNickConsoleEx. It lives in src/ and Harbor.Hosting
        // references it, so it belongs on the matrix like any other Presentation
        // assembly: it may reach Domain (Abstractions facade) + the TUI vocabulary
        // (Terminal.Abstractions) and nothing else. The third-party
        // SharpConsoleUI project it wraps is not a Harbor assembly.
        ["Harbor.Tui.NickConsoleEx"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Terminal.Abstractions"]),

        // ---- Application ----------------------------------------------------
        ["Harbor.Application"] = new(Layer.Application,
            ["Harbor.Abstractions", "Harbor.Diagnostics.Abstractions", "Harbor.Extensions"]),
        ["Harbor.Registries"] = new(Layer.Application, ["Harbor.Abstractions"]),
        ["Harbor.Plugins.Abstractions"] = new(Layer.Application, ["Harbor.Abstractions"]),
        // Runtime is the composition surface over the plugin machinery stack
        // (Host/Storage/Compilation/Instantiation/Registration are its family);
        // classified Infrastructure-plugins rather than Application because of
        // those intra-family edges.
        ["Harbor.Plugins.Runtime"] = new(Layer.Infrastructure,
        [
            "Harbor.Plugins.Hosting", "Harbor.Plugins.Storage",
            "Harbor.Plugins.Compilation", "Harbor.Plugins.Instantiation",
            "Harbor.Plugins.Registration", "Harbor.Plugins.Abstractions",
            "Harbor.Abstractions",
        ]),

        // ---- Infrastructure ---------------------------------------------------
        ["Harbor.Providers.OpenAiCompatible"] = new(Layer.Infrastructure, ["Harbor.Abstractions"]),
        ["Harbor.Providers.Anthropic"] = new(Layer.Infrastructure, ["Harbor.Abstractions"]),
        ["Harbor.Providers.OpenAI"] = new(Layer.Infrastructure, ["Harbor.Abstractions"]),
        ["Harbor.Providers.Ollama"] = new(Layer.Infrastructure, ["Harbor.Abstractions"]),
        ["Harbor.Storage.Jsonl"] = new(Layer.Infrastructure, ["Harbor.Abstractions"]),
        ["Harbor.Storage.Memory"] = new(Layer.Infrastructure, ["Harbor.Abstractions"]),
        ["Harbor.Storage.Sqlite"] = new(Layer.Infrastructure, ["Harbor.Abstractions"]),
        ["Harbor.Tools.Builtin"] = new(Layer.Infrastructure,
            ["Harbor.Abstractions", "Harbor.Extensions"]),
        ["Harbor.Lsp"] = new(Layer.Infrastructure, ["Harbor.Abstractions"]),
        ["Harbor.Terminal.Pty"] = new(Layer.Infrastructure, []),
        ["Harbor.Logging"] = new(Layer.Infrastructure, []),
        ["Harbor.Transport.Remote"] = new(Layer.Infrastructure, ["Harbor.Abstractions"]),
        ["Harbor.Telemetry.Core"] = new(Layer.Infrastructure,
            ["Harbor.Diagnostics.Abstractions", "Harbor.Abstractions"]),
        ["Harbor.Telemetry.Otlp"] = new(Layer.Infrastructure, ["Harbor.Telemetry.Core"]),
        ["Harbor.Ipc.Client"] = new(Layer.Infrastructure,
            ["Harbor.Ipc.Abstractions", "Harbor.Abstractions"]),
        // #450: the IL-based checks proved the Ipc.Abstractions edge is real for
        // InProcess (the in-memory transport binds the shared IPC channel
        // contracts) — an earlier narrowing of this row was wrong and reverted.
        ["Harbor.Ipc.InProcess"] = new(Layer.Infrastructure,
            ["Harbor.Ipc.Abstractions", "Harbor.Abstractions"]),
        // #450: "Harbor.Application" was permitted but never referenced — an
        // Infrastructure → Application edge that was pure dead permission.
        ["Harbor.Ipc.Server"] = new(Layer.Infrastructure,
            ["Harbor.Ipc.Abstractions", "Harbor.Abstractions"]),
        ["Harbor.Plugins.Storage"] = new(Layer.Infrastructure, ["Harbor.Plugins.Abstractions"]),
        ["Harbor.Plugins.Compilation"] = new(Layer.Infrastructure,
            ["Harbor.Plugins.Abstractions", "Harbor.Abstractions"]),
        ["Harbor.Plugins.Instantiation"] = new(Layer.Infrastructure,
            ["Harbor.Plugins.Abstractions", "Harbor.Abstractions"]),
        ["Harbor.Plugins.Registration"] = new(Layer.Infrastructure,
            ["Harbor.Plugins.Abstractions", "Harbor.Plugins.Instantiation", "Harbor.Abstractions"]),
        // #450: Storage / Compilation / Instantiation / Registration were permitted
        // but never bound — PluginHost composes the machinery through the
        // Plugins.Abstractions contracts only. That is exactly the "declares 5
        // targets, uses 1" rot #450 was filed for; the four are removed here and
        // their ProjectReferences are tracked as known-vestigial edges.
        ["Harbor.Plugins.Hosting"] = new(Layer.Infrastructure,
        [
            "Harbor.Plugins.Abstractions",
        ]),
        // ---- CompositionRoot -----------------------------------------------
        ["Harbor.Hosting"] = new(Layer.CompositionRoot,
        [
            // Wires DI over the whole graph incl. contrib renderers — free tier.
            "Harbor.Abstractions", "Harbor.Abstractions.Contracts",
            "Harbor.Diagnostics.Abstractions", "Harbor.Telemetry.Core",
            "Harbor.Application", "Harbor.Registries",
            "Harbor.Desktop.Abstractions",
            "Harbor.Terminal.Abstractions", "Harbor.Ui.Framework.State",
            // #450: "Harbor.Ui.Framework.Sessions" was removed here on the theory
            // that no file named the namespace, but the IL reference is real (the
            // IL gate caught the regression), so it is permitted again.
            "Harbor.Ui.Framework.Sessions",
            "Harbor.Storage.Jsonl", "Harbor.Storage.Memory", "Harbor.Storage.Sqlite",
            "Harbor.Tui.AnsiPlain",
            "Harbor.Tui.CellForge",
            "Harbor.Providers.Ollama", "Harbor.Providers.OpenAiCompatible",
            "Harbor.Providers.Anthropic", "Harbor.Providers.OpenAI",
            "Harbor.Tools.Builtin", "Harbor.Lsp", "Harbor.Ipc.Abstractions",
            "Harbor.Ipc.InProcess", "Harbor.Ipc.Server", "Harbor.Ipc.Client",
            // #450: "Harbor.Plugins.Runtime" and "Harbor.Ui.Framework.Sessions"
            // were permitted but never bound — the composition root composes the
            // plugin family through Plugins.Hosting/Abstractions and the session
            // slice through Hosting's own modules. Dead permission, removed.
            "Harbor.Plugins.Storage",
            "Harbor.Plugins.Compilation", "Harbor.Plugins.Instantiation",
            "Harbor.Plugins.Registration", "Harbor.Plugins.Hosting",
            // Trust gate (IPluginSource/PluginScript contract) composed in RegistriesModule:
            "Harbor.Plugins.Abstractions",
            // contrib/tui renderer references live outside src/:
            "Harbor.Tui.Spectre", "Harbor.Tui.Spectre.Fullscreen",
            "Harbor.Tui.SpectreTui", "Harbor.Tui.TerminalGui",
            "Harbor.Tui.Termina", "Harbor.Tui.RazorConsole",
            // renderer-unification Phase 3: nickprotop/ConsoleEx wrapper,
            // wired behind HarborWithNickConsoleEx (mutually exclusive with
            // HarborWithSpectreTui — see Harbor.Hosting.csproj).
            "Harbor.Tui.NickConsoleEx",
        ]),
    };

    /// <summary>
    ///     Every src assembly the matrix classifies as <see cref="Layer.Presentation" />,
    ///     in declaration order of <see cref="Matrix" />.
    /// </summary>
    /// <remarks>
    ///     Consumed by <c>PresentationCapabilityRules.cs</c> (#455) so the
    ///     Presentation capability rules and the reference matrix can never drift
    ///     apart: a new Presentation src project becomes capability-enforced the
    ///     moment it gets a matrix row — no second list to forget to update.
    /// </remarks>
    public static string[] PresentationLayerAssemblies()
        => Matrix.Where(kv => kv.Value.Layer == Layer.Presentation).Select(kv => kv.Key).ToArray();

    /// <summary>
    ///     Allowed set plus implicit edges:
    ///     - referencing <c>Harbor.Abstractions</c> implies
    ///       <c>Harbor.Abstractions.Contracts</c> (the facade re-exports contract
    ///       types, so consumer IL legitimately emits the Contracts AssemblyRef);
    ///     - plus this row's documented exceptions.
    /// </summary>
    internal static HashSet<string> ExpandAllowed(string name, Row row)
    {
        var allowed = row.Allowed.ToHashSet();
        if (allowed.Contains("Harbor.Abstractions"))
        {
            allowed.Add("Harbor.Abstractions.Contracts");
        }
        if (DocumentedExceptions.TryGetValue(name, out var exc))
        {
            allowed.UnionWith(exc.Select(e => e.Target));
        }
        return allowed;
    }

    /// <summary>
    ///     Every src assembly's ACTUAL Harbor references must be a subset of its
    ///     declared Allowed set (plus documented exceptions). Fails listing each
    ///     unexpected edge, so one run reports all regressions.
    /// </summary>
    [Test]
    public async Task EverySrcAssembly_ReferenceSet_MatchesMatrix()
    {
        var loaded = ArchitectureTestHelpers.LoadHarborAssemblies();
        var failures = new List<string>();

        foreach (string name in AllSrcAssemblies)
        {
            if (!loaded.TryGetValue(name, out var asm))
            {
                failures.Add($"{name}: assembly not loaded — missing ProjectReference in Harbor.Architecture.Tests.csproj?");
                continue;
            }

            if (!Matrix.TryGetValue(name, out var row))
            {
                failures.Add($"{name}: no matrix row");
                continue;
            }

            var allowed = ExpandAllowed(name, row);
            foreach (string actualRef in ArchitectureTestHelpers.GetReferencedAssemblyNames(asm))
            {
                if (!actualRef.StartsWith("Harbor", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!allowed.Contains(actualRef))
                {
                    failures.Add($"{name} -> {actualRef}: not in Allowed set (layer {row.Layer})");
                }
            }
        }

        await Assert.That(failures.Count).IsEqualTo(0)
            .Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Table-level guard: the Allowed sets themselves must respect the layer
    ///     rules, so a future matrix edit cannot smuggle a violation past check 1
    ///     by pre-declaring it as "allowed". Exceptions must go through
    ///     <see cref="DocumentedExceptions" /> with a reason instead.
    /// </summary>
    [Test]
    public async Task MatrixTable_RespectsLayerRules()
    {
        string? Family(string asm) => asm switch
        {
            var n when n.StartsWith("Harbor.Plugins.", StringComparison.Ordinal) => "plugins",
            var n when n.StartsWith("Harbor.Ipc.", StringComparison.Ordinal) => "ipc",
            var n when n.StartsWith("Harbor.Telemetry.", StringComparison.Ordinal) => "telemetry",
            _ => null,
        };

        var layers = Matrix.ToDictionary(kv => kv.Key, kv => kv.Value.Layer);
        var failures = new List<string>();

        foreach (var (from, row) in Matrix)
        {
            foreach (string to in row.Allowed)
            {
                if (!layers.TryGetValue(to, out var toLayer))
                {
                    // contrib renderers referenced by Hosting — out of matrix scope.
                    if (row.Layer == Layer.CompositionRoot) continue;
                    failures.Add($"{from} -> {to}: target has no matrix row");
                    continue;
                }

                bool ok = row.Layer switch
                {
                    Layer.Domain => toLayer == Layer.Domain,
                    Layer.Presentation => toLayer is Layer.Domain or Layer.Presentation,
                    Layer.Application => toLayer is Layer.Domain or Layer.Application,
                    Layer.Infrastructure => toLayer is Layer.Domain or Layer.Application
                        || (toLayer == Layer.Infrastructure && Family(to) == Family(from)),
                    Layer.CompositionRoot => true,
                    _ => false,
                };

                if (!ok)
                {
                    failures.Add(
                        $"{from} ({row.Layer}) -> {to} ({toLayer}): violates layer rules; " +
                        "move to DocumentedExceptions with a reason if legitimate");
                }
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    /// <summary>
    ///     Every exception entry must actually be needed by its row's Allowed
    ///     set complement — i.e. correspond to a real current reference — so the
    ///     exception list cannot rot into blanket permissions.
    /// </summary>
    [Test]
    public async Task DocumentedExceptions_AllCurrentlyRealized()
    {
        var loaded = ArchitectureTestHelpers.LoadHarborAssemblies();
        var failures = new List<string>();

        foreach (var (from, excs) in DocumentedExceptions)
        {
            if (!loaded.TryGetValue(from, out var asm))
            {
                failures.Add($"{from}: assembly not loaded but has exception entries");
                continue;
            }

            var refs = ArchitectureTestHelpers.GetReferencedAssemblyNames(asm);
            foreach (var exc in excs)
            {
                if (!refs.Contains(exc.Target))
                {
                    failures.Add($"{from} -> {exc.Target}: exception is stale (reference no longer exists); remove it");
                }
            }
        }

        await Assert.That(failures.Count).IsEqualTo(0)
            .Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Coverage guard: every assembly named in <see cref="AllSrcAssemblies" />
    ///     must have a matrix row, and vice versa. Adding a src project without
    ///     enforcement fails here loudly instead of silently skipping.
    /// </summary>
    [Test]
    public async Task Matrix_CoversExactlyTheSrcInventory()
    {
        var inventory = AllSrcAssemblies.ToHashSet();
        var rows = Matrix.Keys.ToHashSet();

        var missingRows = inventory.Except(rows).ToList();
        var orphanRows = rows.Except(inventory).ToList();

        await Assert.That(missingRows).IsEmpty();
        await Assert.That(orphanRows).IsEmpty();
    }
}
