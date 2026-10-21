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

        // The architecture rules as compiler extensions (#1842). The same shape
        // as the source generator above: a build-time Roslyn component that
        // every product project consumes with OutputItemType=Analyzer and
        // ReferenceOutputAssembly=false, so it produces no runtime edge and
        // there is nothing to constrain.
        ["Harbor.Arch.Analyzers"] = "Build-time Roslyn analyzer project (the repository's own architecture rules); consumed via OutputItemType=Analyzer, produces no runtime assembly.",

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
    /// <remarks>
    ///     <para>
    ///         #456: the entries are folder → reason (rather than a bare name) because a folder
    ///         whose only defense is its own comment is one refactor from being deleted, and a
    ///         deleted consumer leaves the folder compiling into nothing.
    ///     </para>
    ///     <para>
    ///         <see cref="SharedSourceLinkRules" /> is what holds this list honest: every
    ///         csproj-less source directory must appear here, and every entry must have a
    ///         consumer. Without it the list is a comment with a type — a new shared-source
    ///         folder, or a dropped <c>Compile</c> item, changes nothing a test can see.
    ///     </para>
    /// </remarks>
    internal static readonly Dictionary<string, string> SharedSourceFolders = new(StringComparer.Ordinal)
    {
        // The chat-completions SSE pump and wire parser, shared by four sibling provider
        // assemblies. The matrix forbids Infrastructure→Infrastructure references (see
        // NetArchLayerRules.ForbiddenForInfrastructure, which names all four providers and all
        // three storage backends), so there is no way for one provider to reference another;
        // the single source travels as linked files instead. Linked into all four providers;
        // OpenAiWire and OpenAiImageContent reach only the two OpenAI-format ones, because
        // Anthropic and Ollama have their own wire types.
        ["Harbor.Providers.Shared"] =
            "Four sibling provider assemblies must share one SSE pump and wire parser, and the "
            + "layer matrix forbids Infrastructure→Infrastructure project references, so the code "
            + "is <Compile Include>-linked rather than referenced. Linked into the four providers; "
            + "OpenAiWire/OpenAiImageContent reach only OpenAI + OpenAiCompatible, whose wire "
            + "format Anthropic and Ollama do not share.",

        // #456: this folder was here before the issue was filed and was not on the list at
        // all, so nothing in the enforcer read it. It was declared with the gap spelled out:
        // Harbor.Storage.Memory linked NEITHER file and hand-wrote the same literals ELEVEN
        // times (nine SessionNotFound, two MessageNotFound). #887 closed that — Memory now
        // links SessionStoreErrors.cs, so all three stores share the failure texts — so the
        // reason below states which file reaches which store instead.
        ["Harbor.Storage.Shared"] =
            "The three session stores must share one set of canonical failure texts, and the "
            + "layer matrix forbids Infrastructure→Infrastructure project references, so the "
            + "code is <Compile Include>-linked rather than referenced. SessionStoreErrors "
            + "reaches all three. SessionLockStrip reaches Jsonl and Sqlite; SessionStatsAggregator "
            + "reaches Jsonl alone, because Sqlite and Memory persist the metadata record instead "
            + "of folding it from message history. Memory links SessionStoreErrors and NEITHER of "
            + "the other two: it takes lock (list) on its per-session List rather than a "
            + "SemaphoreSlim stripe over a store-owned dictionary, so there is no strip to share.",
    };

    /// <summary>
    ///     Projects referenced as source generators (OutputItemType=Analyzer).
    ///     A generator is a build-time dependency, not a layer edge, so it is
    ///     excluded from the matrix — but it must be declared here, so that a
    ///     NEW analyzer reference cannot slip past unnoticed.
    /// </summary>
    /// <remarks>
    ///     #1842 adds the second entry: the architecture rules as compiler
    ///     extensions. The declaration covers both shapes on purpose — the
    ///     rule asks "is this analyzer reference a known build-time component",
    ///     and the answer for a generator and for an analyzer is the same.
    /// </remarks>
    internal static readonly string[] SourceGeneratorProjects = ["Harbor.CodeGen", "Harbor.Arch.Analyzers"];

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
        "Harbor.Ui.Framework.Services",
        "Harbor.Ui.Framework.ViewModels",
        "Harbor.Ui.Framework.Projection",
        "Harbor.Ui.Framework.Rendering",
        "Harbor.DesignSystem",
        "Harbor.Ui.Framework.Sessions",
        "Harbor.Desktop.Abstractions",
        // #1899: the concrete desktop view-models split out of
        // Harbor.Desktop.Abstractions, which had become a hub wearing a
        // leaf's name. The leaf keeps the config schema, models and enums;
        // this row is the view-model layer.
        "Harbor.Desktop.ViewModels",
        "Harbor.Desktop.Shared",
        "Harbor.Desktop.Animations",
        "Harbor.Tui.Notifications",
        "Harbor.Tui.AnsiPlain",
        "Harbor.Tui.CellForge.Engine",
        "Harbor.Tui.CellForge",
        // #1617: the syntax-highlight + themes cluster (CodeSyntaxTokenizer /
        // ILanguageSupport family / CodeHighlightPalette / CodeTokenizer facade,
        // JsonThemeLoader / ThemeFileWatcher) as its own Presentation sibling.
        "Harbor.Tui.CellForge.Syntax",
        // #1618: the prompt-engine pair (ListPromptEngine / FormPromptEngine,
        // SC7+SC6 #1215/#1214) as its own Presentation sibling.
        "Harbor.Tui.CellForge.Prompts",
        // Application
        "Harbor.Application", "Harbor.ChangeTracking",
        // #1614: first-run wizard + setup-checklist detection, one-way leaf
        // over Harbor.Application's configuration family (0 reverse edges).
        "Harbor.Onboarding",
        "Harbor.Hooks", "Harbor.Permissions", "Harbor.AgentRuntime",
        // #1625: the configuration family (HarborConfig / JsonConfigStore /
        // AuthStore / ProviderPresets / the normalizer), extracted from
        // Harbor.Application — the last piece of the PolicyConfig cohort.
        "Harbor.PolicyConfig",
        // #1624: session-context cohort (compaction/prompt/tracker + workspace
        // probes + session fork), extracted from Harbor.Application.
        "Harbor.SessionContext",
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
        // from the configuration family. The Harbor.Core facade that used to
        // carry this edge was removed in #188 and the facade itself deleted in #451.
        // #1625: the target moved with the catalog — the configuration family
        // left Harbor.Application for Harbor.PolicyConfig, and the debt is the
        // same one, one assembly over. The fix named below is unchanged.
        // Future fix: move the preset catalog to Domain (Harbor.Abstractions.Providers)
        // — tracked per site, so the debt cannot spread past these two files.
        //
        // #1899: the edge moved with the view-models that read it. The config leaf
        // (Harbor.Desktop.Abstractions) no longer reaches Harbor.Application at
        // all, which is what makes it a leaf.
        ["Harbor.Desktop.ViewModels"] =
        [
            new DocumentedException(
                "Harbor.PolicyConfig",
                "#188/#96 Presentation→Application debt: ProviderPresets catalog. Fix: move presets to Domain (Harbor.Abstractions.Providers).",
                [
                    "ViewModels/OnboardingViewModel.cs",
                    "ViewModels/ProviderModelPickerViewModel.cs",
                ]),
        ],
        // ITuiPlugin / TUI vocabulary lives in Terminal.Abstractions by design;
        // plugin-surface assemblies legitimately reach Presentation for it.
        //
        // #620: IPluginBackendAxes.cs is listed for the same reason and the same
        // reason it is a NAMED site and not a project-granular excuse — the
        // ITuiBackendPlugin axis is defined by its door, and that door's payload
        // is a Func<ITuiRenderer>. It is the renderer equivalent of ITuiPlugin,
        // not a widening: #620's ExtensionAxisRegistrationRule is what found the axis
        // half-open, and this file is the plugin-author half of it.
        ["Harbor.Plugins.Abstractions"] =
        [
            new DocumentedException(
                "Harbor.Terminal.Abstractions",
                "ITuiPlugin and ITuiRenderer are TUI vocabulary; the plugin contract surface must name them.",
                ["IPluginLoadHost.cs", "IPluginBackendAxes.cs"]),
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
                "ITuiPlugin is TUI vocabulary; the registrar registers TUI plugins, and the #1758 recorder forwards the same doors.",
                ["PluginRegistrar.cs", "RecordingPluginLoadHost.cs"]),
            new DocumentedException(
                "Harbor.Ui.Framework.State",
                "Registered TUI plugins carry view-model/state payloads; the #1758 recorder forwards the panel door.",
                ["PanelRegistryPluginAdapter.cs", "PluginRegistrar.cs", "RecordingPluginLoadHost.cs"]),
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
        // #450: the ProjectReference to Harbor.Abstractions is declared but the
        // assembly binds no type from it (the IL gate confirms no AssemblyRef), so
        // the row is narrowed to empty and the reference is listed as
        // declared-but-unbound. An earlier attempt to keep the entry, on the
        // theory that the SDK-generated AssemblyInfo forces the reference, was
        // wrong — the compiled reference list is the authority.
        // #453: the empty set was the honest answer while this project bound no
        // type from anywhere — it was a leaf over BCL and the logging abstractions
        // only. The narrow shared-config port now returns Maybe<ModelRef>, and
        // ModelRef is a type in Harbor.Abstractions.Contracts, so the reference
        // became a REAL IL edge instead of a transitive one. Domain -> Domain,
        // the same direction as Harbor.Abstractions -> Harbor.Abstractions.Contracts
        // below, and the carrier is the whole point of the fix: #729 established
        // ModelRef as the only type in the repo whose two halves are non-null by
        // construction, and a port that hands out a pair of nullable strings
        // cannot be moved onto it without losing the property.
        // #453: the empty set was the honest answer while this project bound no
        // type from anywhere — it was a leaf over BCL and the logging abstractions
        // only. The narrow shared-config port now returns Maybe<ModelRef>, and
        // ModelRef is a type in Harbor.Abstractions.Contracts, so the reference
        // became a REAL IL edge instead of a transitive one. Domain -> Domain,
        // the same direction as Harbor.Abstractions -> Harbor.Abstractions.Contracts
        // below, and the carrier is the whole point of the fix: #729 established
        // ModelRef as the only type in the repo whose two halves are non-null by
        // construction, and a port that hands out a pair of nullable strings
        // cannot be moved onto it without losing the property.
        ["Harbor.Ui.Framework.Abstractions"] = new(Layer.Domain, ["Harbor.Abstractions.Contracts", "Harbor.Extensions"]),

        // ---- Presentation -------------------------------------------------
        // #450: "Harbor.Ui.Framework" was permitted here but never used. The
        // project is an empty meta-package shell (no .cs files at all), so no
        // consumer can bind a type from it and Roslyn never emits the
        // AssemblyRef — the edge was unenforceable permission. Removed; the leaf
        // Ui.Framework.* modules are the real dependencies.
        ["Harbor.Terminal.Abstractions"] = new(Layer.Presentation, ["Harbor.Abstractions"]),
        ["Harbor.Ui.Framework"] = new(Layer.Presentation, []),
        ["Harbor.Ui.Framework.State"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.Abstractions",
                // #33 T1: KeyEventAdapter consumes the BCL-only UiKeyDto key
                // vocabulary (Rendering.Input) — the reversed edge replacing the
                // old Rendering→State one; Presentation→Presentation conforms.
                "Harbor.Ui.Framework.Rendering", "Harbor.Extensions"]),
        // #594 removed the `Harbor.Ui.Framework.Reducers` row outright. It was
        // the second home for the AgentEvent → UI-state fold, and nothing ever
        // built a renderer on it: no composition root constructed its AppStore,
        // so the edge it declared here was permission for a branch with no
        // caller. Its one live-looking consumer, EventBusAppStoreDispatcher, went
        // with it. The rows that remain below are the real dependencies.
        // #667: `FileTreeLoader` implements `IFileTreeLoader`, which lives beside
        // `PanelServices` in the State assembly — so this row already permitted
        // the edge and the reference is genuine, not a new permission. The
        // direction is the point and it is the reason the split was done this
        // way round: the INTERFACE is in State, the implementation in Services.
        // Naming the concrete loader in `PanelServices` would have needed the
        // reverse edge and a project cycle.
        // #1186 T4: SlashCommandProvider projects SlashCommandCatalog and
        // SystemCommandProvider reuses OverlayIds, so Services now binds the
        // Abstractions package in IL. Same Presentation → Domain edge the
        // other Ui.Framework.* rows already declare (#567 precedent).
        ["Harbor.Ui.Framework.Services"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Abstractions", "Harbor.Extensions"]),
        // #567: ToolCallViewModel.Status is a ToolCallState, so the view-models
        // now bind the Abstractions package in IL rather than carrying an
        // unbound reference to it. Same Presentation → Domain edge the other
        // Ui.Framework.* rows already declare.
        // #679: SideBySideDiffViewModel.Compute (renamed from DiffViewModel by
        // #570) projects the core line-level LCS (Rendering.Widgets.LineDiff)
        // instead of comparing left[i]/right[i] itself, so the edge is bound.
        // Direct rather than transitive through State (CF-A-001 precedent) —
        // Presentation → Presentation conforms.
        ["Harbor.Ui.Framework.ViewModels"] = new(Layer.Presentation,
            [ "Harbor.Abstractions.Contracts", "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Services",
              "Harbor.Ui.Framework.Abstractions", "Harbor.Ui.Framework.Rendering"]),
        ["Harbor.Ui.Framework.Projection"] = new(Layer.Presentation,
            [ "Harbor.Abstractions.Contracts", "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Abstractions",
             // RgbColor is defined in the standalone DesignSystem package but
             // keeps its historical Projection namespace for compatibility.
             "Harbor.DesignSystem", "Harbor.Extensions"]),
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
            ["Harbor.DesignSystem", // #75: canonical ctx% helper (ContextUsage) — Presentation → Domain is rule-conforming.
                "Harbor.Abstractions.Contracts", "Harbor.Extensions"]),
        // HDS v1 token catalog — standalone leaf: ZERO Harbor references. The
        // design-system package ships RgbColor (under the historical Projection
        // namespace) plus the cell-style primitives and ChatPalette, so
        // Projection/Rendering/CellForge/apps all resolve them from here.
        ["Harbor.DesignSystem"] = new(Layer.Presentation, []),
        // #663: "Harbor.Ui.Framework.ViewModels" joined this row. It was a
        // #450 dead edge (declared, permitted, bound in no IL) until
        // SessionContext.StatusText started calling
        // StatusMappers.SessionStatusToText instead of keeping its own
        // SessionStatus→label switch. Both projects are Presentation-layer
        // siblings, which the matrix permits; the reference is now live, so
        // EnforcerIntegrityTests demanded the exemption be deleted and the edge
        // be justified here. That is the same shape #567 recorded for
        // ViewModels → Abstractions below.
        ["Harbor.Ui.Framework.Sessions"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Services", "Harbor.Ui.Framework.Abstractions", "Harbor.Ui.Framework.ViewModels", "Harbor.Extensions"]),
        // #450: "Harbor.Terminal.Abstractions" and "Harbor.Ui.Framework" were both
        // permitted but never referenced. Terminal.Abstractions is dead permission
        // (no file names the ITuiRenderer/ITuiView vocabulary); Ui.Framework is an
        // empty assembly (see the Terminal.Abstractions row). The leaf
        // Ui.Framework.* modules below are what this project actually binds.
        //
        // #1899: this row is now a LEAF. The view-model bases and the concrete
        // view-models moved to Harbor.Desktop.ViewModels, and with them the
        // Ui.Framework.* edges, the Terminal.Abstractions edge and the
        // Harbor.Application edge (#188). What is left is the config schema,
        // the config-store ports, the cross-view-model messages and the shared
        // models/enums — and one reference, to Harbor.Abstractions.Contracts.
        ["Harbor.Desktop.Abstractions"] = new(Layer.Presentation,
        [
            // #1899: the contract assembly, not the facade. The leaf's only two
            // Harbor types are SessionStatus and AgentName.Fallback, and both
            // live in Harbor.Abstractions.Contracts. Permitting the facade
            // instead would demand an IL edge to an assembly the leaf does not
            // bind a single type of.
            "Harbor.Abstractions.Contracts",
        ]),
        // #1899: the concrete desktop view-models. They need the Ui.Framework
        // family (the VM bases, the TEA state, the dispatcher/theme services),
        // the config leaf they bind (CommonConfig, ICommonConfigStore,
        // SessionDotState), and — through the documented #188 exception above —
        // Harbor.Application.Configuration.ProviderPresets.
        ["Harbor.Desktop.ViewModels"] = new(Layer.Presentation,
        [
            "Harbor.Abstractions",
            "Harbor.Desktop.Abstractions",
            "Harbor.Ui.Framework.ViewModels",
            "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Services",
            "Harbor.Ui.Framework.Sessions",
            // #680: "Harbor.Ui.Framework.Abstractions" was permitted here because
            // ChatViewModelBase aliased ToolCallState (which lives there) for the
            // one line of its hand-rolled tool-line parser that recovered a status
            // by sniffing a "✗" prefix. With the parser gone the alias went with
            // it, and Matrix_AllowedEntries_AreLive reported the row as stale —
            // which is the guard working: a permitted edge nobody takes is a lie
            // about the assembly's dependencies.
            //
            // #679: the desktop DiffViewModel/DiffViewModelBase read the core
            // diff algorithm and the diff-format parsers
            // (Rendering.Widgets) rather than each re-deriving a diff by index
            // and re-sniffing the format by string literal.
            "Harbor.Ui.Framework.Rendering",
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
        // #665: the renderer now binds INotificationProcessRunner out of the
        // Abstractions facade, so the previously dead (Notifications ->
        // Harbor.Abstractions) edge is a real one — the same shape #567 recorded
        // for Ui.Framework.ViewModels and #663 for Ui.Framework.Sessions. The
        // facade's implicit Harbor.Abstractions.Contracts edge comes with it
        // (ExpandAllowed), so the contract lives in the facade and NOT in
        // .Contracts, where it would have needed no matrix change at all: the
        // facade is where interface contracts belong (see Harbor.Abstractions'
        // own header), and IGitQuery — the precedent for this exact move — is
        // there too.
        ["Harbor.Tui.Notifications"] = new(Layer.Presentation,
            [ "Harbor.Abstractions", "Harbor.Terminal.Abstractions"]),
        // renderer-unification Phase 4: Ansi + Plain merged into one assembly;
        // styling flows through IEscapeCodeStrategy (Ansi / Null impls).
        // Issue #77: chat writes land in the DI-shared UiStore (Presentation→
        // Presentation edge, same as CellForge.Engine).
        ["Harbor.Tui.AnsiPlain"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Terminal.Abstractions", "Harbor.Ui.Framework.State"]),
        // CellForge engine (issue #33 split): input/parsing/capabilities/cell
        // primitives. A standalone leaf: ZERO Harbor references (#436). The
        // vocabulary it uses (cells, keys, probe) lives here now as
        // engine-owned ports (verbatim copies under Harbor.Tui.CellForge.*);
        // the chat-owned rendering (prompt/composer/markdown/overlay/layout/
        // image-layer) moved to Harbor.Tui.CellForge/Chat/Rendering, which
        // references both sides. The host boundary is explicit: ScreenSession
        // paints a Rendering grid and the flush copies it cell-for-cell into
        // the engine staging buffer; keys convert Engine->Rendering once at
        // the REPL loop entry; wheel ticks arrive as the engine UiKeyDto.
        //
        // #435 removed Harbor.Ui.Framework.State + Harbor.Abstractions (the
        // latter dead-by-fact, the former via the #162/#435 BCL vocabularies).
        // #436 removes the last two edges below. The #1009 caveat is checked
        // and cleared: Contracts reached the engine ONLY transitively (State
        // until #435, Rendering->Contracts after); with both edges gone no
        // Harbor assembly is reachable at all — direct or transitive — so the
        // BCL-only claim in the csproj is now simply true.
        ["Harbor.Tui.CellForge.Engine"] = new(Layer.Presentation, []),
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
                "Harbor.DesignSystem", "Harbor.Extensions",
                // #1617: Presentation→Presentation sibling holding the extracted
                // syntax-highlight + themes cluster; realized by
                // AssistantMarkdownBlock binding CodeTokenizer.
                "Harbor.Tui.CellForge.Syntax",
                // #1618: Presentation→Presentation sibling holding the extracted
                // prompt-engine pair; realized by DialogSelectList building
                // ListPromptState<string>.
                "Harbor.Tui.CellForge.Prompts", ]),
        // #1617: Harbor.Tui.CellForge.Syntax — Presentation-layer sibling like
        // Harbor.Tui.CellForge.Engine: the verbatim extraction of the CellForge
        // syntax-highlight + themes cluster (issue body files: CodeSyntaxTokenizer
        // + ILanguageSupport/BuiltinLanguageSupports/LanguageSupportRegistry +
        // CodeHighlightPalette + CodeTokenizer facade, JsonThemeLoader +
        // ThemeFileWatcher). The namespaces stay Harbor.Tui.CellForge.* verbatim,
        // so the consumers' usings did not move with the code. The vocabulary it
        // binds is Rendering (spans paint into Rendering grids — the UIR pin) and
        // DesignSystem (HarborTheme/ThemeJson/TerminalColorPalette/IThemeStore);
        // Harbor.Extensions is the Ensure-guard Domain helper leaf the same way
        // #1615's Permissions row grants it, and the Result/Maybe railway rides
        // CSharpFunctionalExtensions transitively through Extensions→Abstractions
        // while the code binds it directly.
        ["Harbor.Tui.CellForge.Syntax"] = new(Layer.Presentation,
            [ "Harbor.Extensions", "Harbor.Ui.Framework.Rendering", "Harbor.DesignSystem" ]),
        // #1618: Harbor.Tui.CellForge.Prompts — Presentation-layer sibling exactly
        // like the Syntax row above: the verbatim extraction of the CellForge
        // prompt-engine pair (issue body files: ListPromptEngine + FormPromptEngine,
        // the SC7 List-prompt / SC6 TextPrompt port pair behind the type: dialog
        // widgets). The namespace stays Harbor.Tui.CellForge.Widgets verbatim, so
        // DialogFormWidgets kept its usings and only this row + CellForge's mirror
        // edge were recorded. The vocabulary it binds is the key surface
        // (KeyEvent/KeyCode/KeyModifiers.AcceptsTypedChar, one Rendering/Input
        // namespace imported once in the sibling's GlobalUsings.cs) and the
        // Ensure-guard Domain helper leaf — no painting, no palette, no theme
        // store, no Result/Maybe railway: the pair is BCL + two direct edges.
        ["Harbor.Tui.CellForge.Prompts"] = new(Layer.Presentation,
            [ "Harbor.Extensions", "Harbor.Ui.Framework.Rendering" ]),

        // ---- Application ----------------------------------------------------
        // #1625: the configuration family (Configuration/*) left this assembly
        // for Harbor.PolicyConfig, and with it the Harbor.Permissions edge:
        // the only thing that bound the permissions subdomain here was
        // PermissionConfigStoreAdapter, which implements the #1615 seam over
        // IConfigStore and moved with the rest of Configuration/. Nothing in
        // the residue names a Harbor.Permissions type, so the edge is gone
        // rather than merely unused.
        ["Harbor.Application"] = new(Layer.Application,
            ["Harbor.Abstractions", "Harbor.Extensions"]),
        // #1613: Harbor.Hooks — Application-layer sibling like Harbor.Registries:
        // the hook runner binds ToolResult (Domain) + BCL + Logging only; edge is Contracts (Hooks resolves no facade-level type).
        ["Harbor.Hooks"] = new(Layer.Application, ["Harbor.Abstractions.Contracts"]),
        // #1616: verified-change pipeline, verbatim extract from Harbor.Application;
        // reads (never re-exports) its run vocabulary — Application keeps it so Application stays a leaf over Abstractions.
        ["Harbor.ChangeTracking"] = new(Layer.Application, ["Harbor.Application", "Harbor.Extensions", "Harbor.Abstractions.Contracts",
            // #1624: the run records (IsolatedWorkspace / RunManifest / RunState) moved
            // with the session-context cohort.
            "Harbor.SessionContext"]),
        // #1615: Harbor.Permissions — Application-layer sibling like Harbor.Registries,
        // the verbatim extraction of Application/Permissions. PermissionService /
        // ApprovalCoordinator / the path policies bind the contracts-assembly
        // vocabulary, the Harbor.Extensions Ensure.* leaf (Domain, the same edge
        // Harbor.Application's own row carries) plus BCL/Logging only; the
        // whole-config IConfigStore surface is cut at the seam and arrives
        // pre-adapted as IPermissionConfigStore.
        // The Harbor.Application row above grants the mirror edge — realized in IL by
        // PermissionConfigStoreAdapter implementing the subdomain's seam
        // (Application→Application is allowed by MatrixTable_RespectsLayerRules).
        ["Harbor.Permissions"] = new(Layer.Application,
            ["Harbor.Abstractions", "Harbor.Extensions"]),
        // #1614: Harbor.Onboarding — Application-layer one-way leaf like Hooks, but
        // with a real edge INTO the configuration family: the wizard binds
        // AuthStore, IConfigStore, JsonConfigStore and ProviderPresets (the issue's
        // "→Configuration" arrow). 0 reverse edges: nothing in src/ names the leaf
        // except the composition root (Harbor.Hosting registers the wizard in
        // CoreModule). The Contracts edge derives from Harbor.Abstractions
        // (ProviderId/ModelRef) via ExpandAllowed.
        // #1625: that configuration family left Harbor.Application for
        // Harbor.PolicyConfig, so the one edge the leaf carries now points
        // there instead — the leaf never reached into Harbor.Application for
        // anything else.
        ["Harbor.Onboarding"] = new(Layer.Application,
            ["Harbor.PolicyConfig", "Harbor.Abstractions"]),
        // #1625: Harbor.PolicyConfig — the configuration family (HarborConfig,
        // JsonConfigStore, AuthStore, ProviderPresets, the normalizer, the
        // step-budget projection), extracted verbatim from
        // Harbor.Application/Configuration. It binds the contract vocabulary
        // (AgentDefinition, ProviderId / ModelRef / AgentName, PermissionRule),
        // the Harbor.Extensions Ensure.* leaf (Domain) and the #1615 seam port
        // IPermissionConfigStore — the one edge INTO Harbor.Permissions, which
        // is the shape of a subdomain seam: the port is declared by the
        // permissions side and implemented here.
        ["Harbor.PolicyConfig"] = new(Layer.Application,
            ["Harbor.Abstractions", "Harbor.Permissions"]),
        ["Harbor.AgentRuntime"] = new(Layer.Application, ["Harbor.Abstractions", "Harbor.Diagnostics.Abstractions", "Harbor.Extensions",
            // #1623: the extracted loop binds IHookRunner (UserHookGates, CompactionBehavior,
            // ToolDispatcher) — the edge Application keeps on its own row, plus Diagnostics
            // Abstractions for the telemetry measurements the loop publishes.
            "Harbor.Hooks", "Harbor.Application",
            // #1624: the loop reads the session-context cohort — CompactionView,
            // CachingSystemPromptBuilder, MessageConverter, WorkspaceContextSource.
            "Harbor.SessionContext"]),
        // #1624: session-context cohort (Sessions/* — CompactionService, CompactionPolicy,
        // CompactionView, SystemPromptBuilder, CachingSystemPromptBuilder, MessageConverter,
        // TokenTracker, WorkspaceContextSource, WorkspaceInspector, SessionForkService,
        // IsolatedWorkspace, ToolResultContextTrim — plus the ISkillProvider default),
        // verbatim extract from Harbor.Application, one-way edge; reads the residue
        // vocabulary (Resilience contracts for the summary retry, the workspace-context
        // service) via the Application reference and nothing else. Application binds no
        // moved type, so the edge cannot close into a cycle.
        ["Harbor.SessionContext"] = new(Layer.Application, ["Harbor.Abstractions", "Harbor.Extensions",
            "Harbor.Application"]),
        // #1623: agent loop (Agents/AgentLoop..RunBudgetTracker, Agents/Pipeline/*, Resilience impls) —
        // verbatim extract from Harbor.Application, one-way edge; reads the loop vocabulary
        // (Hooks/Sessions/Resources/Telemetry/Resilience contracts) via the Application reference and nothing else.
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
        // #672: the PTY host owns the spawn, so it also owns the seam the desktop
        // pane launches through and the PermissionRuleset that gates it. That
        // contract (ITerminalPaneLauncher) and PermissionRuleset are both in the
        // Domain facade, which Infrastructure may reference — the direction the
        // whole Infrastructure section already uses. Application cannot hold the
        // implementation instead: it sits a layer ABOVE this assembly.
        ["Harbor.Terminal.Pty"] = new(Layer.Infrastructure, ["Harbor.Abstractions"]),
        ["Harbor.Logging"] = new(Layer.Infrastructure, []),
        ["Harbor.Transport.Remote"] = new(Layer.Infrastructure, ["Harbor.Abstractions.Contracts"]),
        ["Harbor.Telemetry.Core"] = new(Layer.Infrastructure,
            ["Harbor.Diagnostics.Abstractions", "Harbor.Abstractions"]),
        // #450: the IL gate proved this assembly emits no AssemblyRef for
        // Telemetry.Core, so the permission was dead weight.
        ["Harbor.Telemetry.Otlp"] = new(Layer.Infrastructure, []),
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
        // #1734: the manifest reader reads the configuration schema a plugin
        // declares (PluginConfigSchema, PluginConfigField, PluginConfigValue).
        // That vocabulary is contract vocabulary, and the sibling plugin layers
        // (Compilation, Instantiation, Registration) already reference it: the
        // storage layer was the only one of the family that did not, because
        // until now it had nothing of its own to read.
        ["Harbor.Plugins.Storage"] = new(Layer.Infrastructure,
            ["Harbor.Plugins.Abstractions", "Harbor.Abstractions"]),
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
            "Harbor.Application", "Harbor.Onboarding", "Harbor.Hooks", "Harbor.Permissions", "Harbor.AgentRuntime", "Harbor.SessionContext", "Harbor.Registries",
            // #1625: the configuration family — ConfigurationModule registers the
            // config stores and the #1615 PermissionConfigStoreAdapter.
            "Harbor.PolicyConfig",
            "Harbor.Desktop.Abstractions",
            "Harbor.Terminal.Abstractions", "Harbor.Ui.Framework.State",
            // #667: FileTreeLoader, registered in TuiModule. First direct edge
            // from the composition root to Ui.Framework.Services; the reference
            // is real (TuiModule names the type), so the IL gate would redden
            // if this permission were dropped.
            "Harbor.Ui.Framework.Services",
            // #450: "Harbor.Ui.Framework.Sessions" was removed here on the theory
            // that no file named the namespace, but the IL reference is real (the
            // IL gate caught the regression), so it is permitted again.
            "Harbor.Ui.Framework.Sessions",
            "Harbor.Storage.Jsonl", "Harbor.Storage.Memory", "Harbor.Tui.AnsiPlain",
            "Harbor.Tui.CellForge",
            // #536: the theme marketplace's persistence half — ThemeStore and
            // ThemeDirectoryWatcher, implementing the IThemeStore port that stayed
            // in the DesignSystem leaf. It is an edge UPWARD on purpose: the leaf
            // cannot hold the I/O (it is IsPackable with an EMPTY allowed set), and
            // Infrastructure may not reference Presentation, so the composition root
            // is the layer the move fits. Application cannot hold it either — it sits
            // below Infrastructure, and the whole Infrastructure section already uses
            // "outer → inner" edges. The reference is real (Harbor.Hosting.Themes
            // names HarborTheme, ThemeJson and IThemeStore), so the IL gate would
            // redden if this permission were dropped.
            "Harbor.DesignSystem",
            "Harbor.Providers.Ollama", "Harbor.Providers.OpenAiCompatible", "Harbor.Tools.Builtin", "Harbor.Lsp", "Harbor.Ipc.Abstractions",
            "Harbor.Ipc.InProcess", "Harbor.Ipc.Server", "Harbor.Ipc.Client",
            // #450: "Harbor.Plugins.Runtime" and "Harbor.Ui.Framework.Sessions"
            // were permitted but never bound — the composition root composes the
            // plugin family through Plugins.Hosting/Abstractions and the session
            // slice through Hosting's own modules. Dead permission, removed.
            // #1055s3: Storage/Compilation/Instantiation/Registration were the
            // in-process compile pipeline; the composition root binds none of
            // them any more, so only the Hosting edge below stays.
            "Harbor.Plugins.Hosting",
            // LoadedPlugin handle (StartupPluginLoad/PluginReloadService):
            "Harbor.Plugins.Abstractions",
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
