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
// Out of scope by design:
//   - Harbor.CodeGen — build tool, not part of Harbor.slnx.
//   - Harbor.Plugins.Host — OutputType=Exe out-of-process MCP stdio server
//     (assembly 'harbor-plugins-host'): an app/composition-root like apps/*.
//   - Harbor.Providers.Shared — shared-source folder (no .csproj): SsePump.cs /
//     OpenAiWire.cs are <Compile Include> linked into the four provider
//     assemblies, so there is no separate assembly to put on the matrix.
//   - apps/* entry points (Harbor.App.Cli, Harbor.App.Avalonia) — composition
//     roots are allowed to reference anything; there is nothing to forbid.

namespace Harbor.Architecture.Tests;

public class FullLayerMatrixTests
{
    /// <summary>Layers a src assembly can belong to.</summary>
    private enum Layer
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

    private sealed record Row(Layer Layer, string[] Allowed);

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
        "Harbor.Tui.Abstractions",
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
        "Harbor.Tui.Plain",
        "Harbor.Tui.Ansi",
        "Harbor.Tui.CellForge",
        // Application
        "Harbor.Application",
        "Harbor.Registries",
        "Harbor.Plugins.Abstractions",
        "Harbor.Plugins.Runtime",
        "Harbor.Core", // empty backward-compat facade over Application+Registries
        // Infrastructure
        "Harbor.Providers.OpenAiCompatible",
        "Harbor.Providers.Anthropic",
        "Harbor.Providers.OpenAI",
        "Harbor.Providers.Ollama",
        "Harbor.Storage.Jsonl",
        "Harbor.Storage.Memory",
        "Harbor.Storage.Sqlite",
        "Harbor.Tools.Builtin",
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

    // (from → to) edges that violate the naive layer rules but are accepted,
    // each with its reason. Anything NOT listed here is a hard failure.
    private static readonly Dictionary<string, string[]> DocumentedExceptions = new()
    {
        // Known tech debt (docs/ROADMAP.md "Circular project reference workaround"):
        // Desktop.Abstractions pulls agent/config vocabulary through the Core
        // facade AND directly from Application (IL-level edge). Tracked for
        // resolution (merge into Ui.Framework or split Terminal.Abstractions).
        ["Harbor.Desktop.Abstractions"] =
        [
            // NOTE: the csproj also declares Harbor.Core (facade), but no Core
            // type survives in IL — only the Application edge is real.
            "Harbor.Application",
        ],
        // ITuiPlugin / TUI vocabulary lives in Terminal.Abstractions by design;
        // plugin-surface assemblies legitimately reach Presentation for it.
        ["Harbor.Plugins.Abstractions"] =
        [
            "Harbor.Terminal.Abstractions",
            // Plugin manifests describe TUI state projections.
            "Harbor.Ui.Framework.State",
        ],
        ["Harbor.Plugins.Compilation"] =
        [
            // Compile-time reference passing includes ITuiPlugin vocabulary.
            "Harbor.Terminal.Abstractions",
        ],
        ["Harbor.Plugins.Registration"] =
        [
            "Harbor.Terminal.Abstractions",
            // Registered TUI plugins carry view-model/state payloads.
            "Harbor.Ui.Framework.State",
        ],
    };

    private static readonly Dictionary<string, Row> Matrix = new()
    {
        // ---- Domain -------------------------------------------------------
        ["Harbor.Abstractions.Contracts"] = new(Layer.Domain, []),
        ["Harbor.Diagnostics.Abstractions"] = new(Layer.Domain, []),
        ["Harbor.Extensions"] = new(Layer.Domain, []),
        ["Harbor.Abstractions"] = new(Layer.Domain, ["Harbor.Abstractions.Contracts"]),
        ["Harbor.Ipc.Abstractions"] = new(Layer.Domain, ["Harbor.Abstractions"]),
        ["Harbor.Ui.Framework.Abstractions"] = new(Layer.Domain, ["Harbor.Abstractions"]),

        // ---- Presentation -------------------------------------------------
        ["Harbor.Terminal.Abstractions"] = new(Layer.Presentation, ["Harbor.Abstractions", "Harbor.Ui.Framework"]),
        ["Harbor.Tui.Abstractions"] = new(Layer.Presentation,
            ["Harbor.Ui.Framework", "Harbor.Terminal.Abstractions"]),
        ["Harbor.Ui.Framework"] = new(Layer.Presentation,
        [
            "Harbor.Ui.Framework.Abstractions", "Harbor.Ui.Framework.State",
            "Harbor.Ui.Framework.Services", "Harbor.Ui.Framework.ViewModels",
            "Harbor.Ui.Framework.Projection", "Harbor.Ui.Framework.Sessions",
        ]),
        ["Harbor.Ui.Framework.State"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.Abstractions"]),
        ["Harbor.Ui.Framework.Reducers"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.State"]),
        ["Harbor.Ui.Framework.Services"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Reducers", "Harbor.Ui.Framework.Abstractions"]),
        ["Harbor.Ui.Framework.ViewModels"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Services", "Harbor.Ui.Framework.Abstractions"]),
        ["Harbor.Ui.Framework.Projection"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Abstractions"]),
        // Renderer-agnostic shared layer: cell/screen primitives, input
        // vocabulary and chat widgets consumed by every renderer backend.
        // Leaf Presentation library over the projection primitives; the HDS
        // token catalog (DesignSystem) and motion tokens (Desktop.Animations)
        // back ChatPalette/PanelFx.
        ["Harbor.Ui.Framework.Rendering"] = new(Layer.Presentation,
            ["Harbor.Ui.Framework.Projection", "Harbor.DesignSystem", "Harbor.Desktop.Animations"]),
        // HDS v1 token catalog — leaf Presentation library over the projection
        // primitives (RgbColor); consumed by Desktop.Animations / CellForge / apps.
        ["Harbor.DesignSystem"] = new(Layer.Presentation,
            ["Harbor.Ui.Framework.Projection"]),
        ["Harbor.Ui.Framework.Sessions"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Services", "Harbor.Ui.Framework.ViewModels", "Harbor.Ui.Framework.Abstractions"]),
        ["Harbor.Desktop.Abstractions"] = new(Layer.Presentation,
        [
            "Harbor.Abstractions", "Harbor.Terminal.Abstractions",
            "Harbor.Ui.Framework", "Harbor.Ui.Framework.ViewModels",
            "Harbor.Ui.Framework.State", "Harbor.Ui.Framework.Services",
            "Harbor.Ui.Framework.Sessions",
        ]),
        ["Harbor.Desktop.Shared"] = new(Layer.Presentation,
            ["Harbor.Desktop.Abstractions", "Harbor.Ui.Framework"]),
        // RgbColor resolves its AssemblyRef directly to the projection primitive
        // library even though token types come through Harbor.DesignSystem.
        ["Harbor.Desktop.Animations"] = new(Layer.Presentation,
            ["Harbor.DesignSystem", "Harbor.Ui.Framework.Projection"]),
        ["Harbor.Tui.Notifications"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Terminal.Abstractions"]),
        ["Harbor.Tui.Plain"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Terminal.Abstractions"]),
        ["Harbor.Tui.Ansi"] = new(Layer.Presentation,
            ["Harbor.Abstractions", "Harbor.Terminal.Abstractions"]),
        // CellForge owns its own input+render stack; reuses Presentation-state
        // streaming buffers (StreamingSync/ChunkedBuffer) and the shared
        // renderer-agnostic layer. Terminal.Abstractions supplies the
        // ITuiRenderer/BaseTuiRenderer adapter surface (Phase 2).
        // DesignSystem supplies the HDS v1 token catalog for ChatPalette;
        // Desktop.Animations supplies the motion tokens (PanelFx); the
        // projection edge is RgbColor's AssemblyRef via that same bridge.
        ["Harbor.Tui.CellForge"] = new(Layer.Presentation,
            [
                "Harbor.Abstractions", "Harbor.Terminal.Abstractions",
                "Harbor.Ui.Framework.State",
                "Harbor.Ui.Framework.Projection",
                "Harbor.Ui.Framework.Rendering",
                "Harbor.DesignSystem", "Harbor.Desktop.Animations",
            ]),

        // ---- Application ----------------------------------------------------
        ["Harbor.Application"] = new(Layer.Application,
            ["Harbor.Abstractions", "Harbor.Diagnostics.Abstractions", "Harbor.Extensions"]),
        ["Harbor.Registries"] = new(Layer.Application, ["Harbor.Abstractions"]),
        // Empty backward-compat facade forwarding to Application+Registries.
        ["Harbor.Core"] = new(Layer.Application, ["Harbor.Application", "Harbor.Registries"]),
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
        ["Harbor.Logging"] = new(Layer.Infrastructure, []),
        ["Harbor.Transport.Remote"] = new(Layer.Infrastructure, ["Harbor.Abstractions"]),
        ["Harbor.Telemetry.Core"] = new(Layer.Infrastructure,
            ["Harbor.Diagnostics.Abstractions", "Harbor.Abstractions"]),
        ["Harbor.Telemetry.Otlp"] = new(Layer.Infrastructure, ["Harbor.Telemetry.Core"]),
        ["Harbor.Ipc.Client"] = new(Layer.Infrastructure,
            ["Harbor.Ipc.Abstractions", "Harbor.Abstractions"]),
        ["Harbor.Ipc.InProcess"] = new(Layer.Infrastructure,
            ["Harbor.Ipc.Abstractions", "Harbor.Abstractions"]),
        ["Harbor.Ipc.Server"] = new(Layer.Infrastructure,
            ["Harbor.Ipc.Abstractions", "Harbor.Abstractions", "Harbor.Application"]),
        ["Harbor.Plugins.Storage"] = new(Layer.Infrastructure, ["Harbor.Plugins.Abstractions"]),
        ["Harbor.Plugins.Compilation"] = new(Layer.Infrastructure,
            ["Harbor.Plugins.Abstractions", "Harbor.Abstractions"]),
        ["Harbor.Plugins.Instantiation"] = new(Layer.Infrastructure,
            ["Harbor.Plugins.Abstractions", "Harbor.Abstractions"]),
        ["Harbor.Plugins.Registration"] = new(Layer.Infrastructure,
            ["Harbor.Plugins.Abstractions", "Harbor.Plugins.Instantiation", "Harbor.Abstractions"]),
        ["Harbor.Plugins.Hosting"] = new(Layer.Infrastructure,
        [
            "Harbor.Plugins.Abstractions", "Harbor.Plugins.Storage",
            "Harbor.Plugins.Compilation", "Harbor.Plugins.Instantiation",
            "Harbor.Plugins.Registration",
        ]),
        // ---- CompositionRoot -----------------------------------------------
        ["Harbor.Hosting"] = new(Layer.CompositionRoot,
        [
            // Wires DI over the whole graph incl. contrib renderers — free tier.
            "Harbor.Abstractions", "Harbor.Abstractions.Contracts",
            "Harbor.Diagnostics.Abstractions", "Harbor.Telemetry.Core",
            "Harbor.Core", "Harbor.Application", "Harbor.Registries",
            "Harbor.Desktop.Abstractions",
            "Harbor.Terminal.Abstractions", "Harbor.Ui.Framework.State",
            "Harbor.Storage.Jsonl", "Harbor.Storage.Memory", "Harbor.Storage.Sqlite",
            "Harbor.Tui.Plain", "Harbor.Tui.Ansi",
            "Harbor.Tui.CellForge",
            "Harbor.Providers.Ollama", "Harbor.Providers.OpenAiCompatible",
            "Harbor.Providers.Anthropic", "Harbor.Providers.OpenAI",
            "Harbor.Tools.Builtin", "Harbor.Ipc.Abstractions",
            "Harbor.Ipc.InProcess", "Harbor.Ipc.Server", "Harbor.Ipc.Client",
            "Harbor.Plugins.Runtime", "Harbor.Plugins.Storage",
            "Harbor.Plugins.Compilation", "Harbor.Plugins.Instantiation",
            "Harbor.Plugins.Registration", "Harbor.Plugins.Hosting",
            // Trust gate (IPluginSource/PluginScript contract) composed in RegistriesModule:
            "Harbor.Plugins.Abstractions",
            // contrib/tui renderer references live outside src/:
            "Harbor.Tui.Spectre", "Harbor.Tui.Spectre.Fullscreen",
            "Harbor.Tui.SpectreTui", "Harbor.Tui.TerminalGui",
            "Harbor.Tui.Termina", "Harbor.Tui.RazorConsole",
        ]),
    };

    /// <summary>
    ///     Allowed set plus implicit edges:
    ///     - referencing <c>Harbor.Abstractions</c> implies
    ///       <c>Harbor.Abstractions.Contracts</c> (the facade re-exports contract
    ///       types, so consumer IL legitimately emits the Contracts AssemblyRef);
    ///     - plus this row's documented exceptions.
    /// </summary>
    private static HashSet<string> ExpandAllowed(string name, Row row)
    {
        var allowed = row.Allowed.ToHashSet();
        if (allowed.Contains("Harbor.Abstractions"))
        {
            allowed.Add("Harbor.Abstractions.Contracts");
        }
        if (DocumentedExceptions.TryGetValue(name, out var exc))
        {
            allowed.UnionWith(exc);
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
            foreach (string to in excs)
            {
                if (!refs.Contains(to))
                {
                    failures.Add($"{from} -> {to}: exception is stale (reference no longer exists); remove it");
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
