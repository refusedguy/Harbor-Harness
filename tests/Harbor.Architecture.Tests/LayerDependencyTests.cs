using Harbor.Abstractions.Models;
using Harbor.Application.Agents;
using Harbor.Core.Tools;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Runtime;
using Harbor.Providers.Anthropic;
using Harbor.Providers.Ollama;
using Harbor.Providers.OpenAI;
using Harbor.Providers.OpenAiCompatible;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Storage.Sqlite;
using Harbor.Tools.Builtin;
using Harbor.Tui.Ansi;
using Harbor.Tui.Plain;
using Harbor.Ui.Framework.State;
using FacadeMarker = Harbor.Core.FacadeMarker;
// AgentLoop — now lives in Harbor.Application.dll, kept in Harbor.Application.Agents namespace for backward compat
// InMemoryMcpRegistry — now lives in Harbor.Registries.dll, kept in Harbor.Core.Tools namespace for backward compat
// Alternative TUI renderers (Spectre/Fullscreen/SpectreTui/TerminalGui/Termina/RazorConsole)
// moved to contrib/tui in sprint 2 — outside the main solution's layer scope.

namespace Harbor.Architecture.Tests;
/// <summary>
///     Layer-dependency tests — mechanically enforce the layering invariants
///     declared in <c>docs/ARCHITECTURE_LAYERS.md</c> §2 (the canonical
///     allowed/forbidden reference matrix).
/// </summary>
/// <remarks>
///     <para>
///         Each test loads a Harbor assembly via a type defined in it, then calls
///         <see cref="Assembly.GetReferencedAssemblies" /> to verify the dependency
///         direction. The tests intentionally use plain reflection — no NetArchTest
///         or Mono.Cecil dependency — so the test project itself stays minimal.
///     </para>
///     <para>
///         <b>Why these tests exist:</b> the previous build had stale
///         <c>&lt;ProjectReference Include="..\Harbor.Core\..." /&gt;</c> entries in
///         every Infrastructure project (Providers.*, Storage.*) and in
///         Harbor.Scripting, even though no <c>using Harbor.Core.*</c> appeared in
///         the code. These vestigial references violated the Clean Architecture
///         rule "Infrastructure depends on Domain only". The architecture tests
///         catch such regressions before they reach <c>main</c>.
///     </para>
/// </remarks>
public class LayerDependencyTests
{
    // The set of "Application or Infrastructure" Harbor assemblies that the Domain
    // layer (Harbor.Abstractions, Harbor.Terminal.Abstractions) must NOT reference.
    // Harbor.Application and Harbor.Registries are Application-layer assemblies
    // that the Domain layer must also not reference.
    private static readonly string[] NonDomainHarborAssemblies =
    [
        "Harbor.Core",
        "Harbor.Application",
        "Harbor.Registries",
        "Harbor.Plugins.Runtime",
        "Harbor.Scripting",
        "Harbor.Providers.OpenAiCompatible",
        "Harbor.Providers.Anthropic",
        "Harbor.Providers.OpenAI",
        "Harbor.Providers.Ollama",
        "Harbor.Storage.Jsonl",
        "Harbor.Storage.Memory",
        "Harbor.Storage.Sqlite",
        "Harbor.Tools.Builtin",
        "Harbor.Cli"
    ];

    // The set of Harbor assemblies that Infrastructure projects must NOT reference.
    // (Infrastructure → Domain only; never Application, never other Infrastructure,
    // never Presentation.)
    private static readonly string[] NonDomainNonSelfHarborAssemblies =
    [
        "Harbor.Core",
        "Harbor.Application",
        "Harbor.Registries",
        "Harbor.Plugins.Runtime",
        "Harbor.Scripting",
        "Harbor.Cli"
    ];

    /// <summary>
    ///     Harbor.Abstractions (Domain) must reference ZERO other Harbor assemblies.
    /// </summary>
    [Test]
    public async Task Abstractions_HasNoHarborProjectReferences()
    {
        var asm = typeof(Session).Assembly;
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, NonDomainHarborAssemblies);
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Harbor.Terminal.Abstractions (Domain) may reference Harbor.Abstractions but
    ///     nothing else from Harbor.
    /// </summary>
    [Test]
    public async Task TuiAbstractions_ReferencesOnlyAbstractions()
    {
        var asm = typeof(UiStore).Assembly;
        string[] forbidden = NonDomainHarborAssemblies
            .Where(n => n != "Harbor.Abstractions")
            .ToArray();
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, forbidden);
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Harbor.Application (use-case layer, split out of Harbor.Core) may reference
    ///     Harbor.Abstractions only — NOT Harbor.Registries, NOT Harbor.Core (would
    ///     re-create the god-project), NOT Tui.Abstractions, NOT any Infrastructure /
    ///     Presentation project, NOT sibling Application projects (Plugins.Runtime,
    ///     Scripting). Verifies the Application-half of the Harbor.Core split keeps
    ///     a clean outward-only dependency direction.
    /// </summary>
    [Test]
    public async Task Application_ReferencesOnlyAbstractions()
    {
        var asm = typeof(AgentLoop).Assembly;
        string[] forbidden =
        [
            "Harbor.Core",
            "Harbor.Registries",
            "Harbor.Terminal.Abstractions",
            "Harbor.Plugins.Runtime",
            "Harbor.Plugins.Abstractions",
            "Harbor.Plugins.Storage",
            "Harbor.Plugins.Compilation",
            "Harbor.Plugins.Instantiation",
            "Harbor.Plugins.Registration",
            "Harbor.Plugins.Hosting",
            "Harbor.Scripting",
            "Harbor.Scripting.Abstractions",
            "Harbor.Scripting.Storage",
            "Harbor.Scripting.Compilation",
            "Harbor.Scripting.Engines",
            "Harbor.Scripting.Bridge",
            "Harbor.Scripting.Hosting",
            "Harbor.Providers.OpenAiCompatible",
            "Harbor.Providers.Anthropic",
            "Harbor.Providers.OpenAI",
            "Harbor.Providers.Ollama",
            "Harbor.Storage.Jsonl",
            "Harbor.Storage.Memory",
            "Harbor.Storage.Sqlite",
            "Harbor.Tools.Builtin",
            "Harbor.Cli"
        ];
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, forbidden);
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Harbor.Registries (registry implementations, split out of Harbor.Core) may
    ///     reference Harbor.Abstractions only — NOT Harbor.Application, NOT
    ///     Harbor.Core (would re-create the god-project), NOT Tui.Abstractions, NOT
    ///     any Infrastructure / Presentation project, NOT sibling Application
    ///     projects. Verifies the Infrastructure-half of the Harbor.Core split keeps
    ///     a clean outward-only dependency direction.
    /// </summary>
    [Test]
    public async Task Registries_ReferencesOnlyAbstractions()
    {
        var asm = typeof(InMemoryMcpRegistry).Assembly;
        string[] forbidden =
        [
            "Harbor.Core",
            "Harbor.Application",
            "Harbor.Terminal.Abstractions",
            "Harbor.Plugins.Runtime",
            "Harbor.Plugins.Abstractions",
            "Harbor.Plugins.Storage",
            "Harbor.Plugins.Compilation",
            "Harbor.Plugins.Instantiation",
            "Harbor.Plugins.Registration",
            "Harbor.Plugins.Hosting",
            "Harbor.Scripting",
            "Harbor.Scripting.Abstractions",
            "Harbor.Scripting.Storage",
            "Harbor.Scripting.Compilation",
            "Harbor.Scripting.Engines",
            "Harbor.Scripting.Bridge",
            "Harbor.Scripting.Hosting",
            "Harbor.Providers.OpenAiCompatible",
            "Harbor.Providers.Anthropic",
            "Harbor.Providers.OpenAI",
            "Harbor.Providers.Ollama",
            "Harbor.Storage.Jsonl",
            "Harbor.Storage.Memory",
            "Harbor.Storage.Sqlite",
            "Harbor.Tools.Builtin",
            "Harbor.Cli"
        ];
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, forbidden);
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Harbor.Core (now a thin backward-compat facade) may reference
    ///     Harbor.Application + Harbor.Registries + Harbor.Abstractions but NOT
    ///     Harbor.Terminal.Abstractions (TUI vocabulary stays out of the agent harness)
    ///     and NOT any Infrastructure / Presentation project. The facade must not
    ///     reach past the Application/Registries layers it forwards.
    /// </summary>
    [Test]
    public async Task Core_ReferencesOnlyApplicationAndRegistriesAndAbstractions()
    {
        // Harbor.Core.dll itself no longer defines AgentLoop (it moved to
        // Harbor.Application.dll), but it transitively references it. We probe
        // the Harbor.Core assembly by name to avoid coupling this test to the
        // type's new home.
        var asm = ArchitectureTestHelpers.LoadHarborAssemblies()["Harbor.Core"]
                  ?? throw new InvalidOperationException(
                      "Harbor.Core assembly was not loaded into the AppDomain; " +
                      "the test project's ProjectReference to Harbor.Core.csproj may be missing.");
        string[] forbidden =
        [
            "Harbor.Terminal.Abstractions",
            "Harbor.Plugins.Runtime",
            "Harbor.Plugins.Abstractions",
            "Harbor.Plugins.Storage",
            "Harbor.Plugins.Compilation",
            "Harbor.Plugins.Instantiation",
            "Harbor.Plugins.Registration",
            "Harbor.Plugins.Hosting",
            "Harbor.Scripting",
            "Harbor.Scripting.Abstractions",
            "Harbor.Scripting.Storage",
            "Harbor.Scripting.Compilation",
            "Harbor.Scripting.Engines",
            "Harbor.Scripting.Bridge",
            "Harbor.Scripting.Hosting",
            "Harbor.Providers.OpenAiCompatible",
            "Harbor.Providers.Anthropic",
            "Harbor.Providers.OpenAI",
            "Harbor.Providers.Ollama",
            "Harbor.Storage.Jsonl",
            "Harbor.Storage.Memory",
            "Harbor.Storage.Sqlite",
            "Harbor.Tools.Builtin",
            "Harbor.Cli"
        ];
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, forbidden);
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Harbor.Plugins.Runtime (Application) may reference Harbor.Abstractions
    ///     and Harbor.Terminal.Abstractions (it needs ITuiPlugin for plugin-contributed
    ///     panels) but NOT Harbor.Core / Harbor.Application / Harbor.Registries
    ///     (Application must not cross-reference), NOT Harbor.Scripting, NOT
    ///     Infrastructure, NOT Presentation.
    /// </summary>
    [Test]
    public async Task PluginsRuntime_ReferencesOnlyAbstractions()
    {
        var asm = typeof(PluginHost).Assembly;
        string[] forbidden =
        [
            "Harbor.Core",
            "Harbor.Application",
            "Harbor.Registries",
            "Harbor.Scripting",
            "Harbor.Providers.OpenAiCompatible",
            "Harbor.Providers.Anthropic",
            "Harbor.Providers.OpenAI",
            "Harbor.Providers.Ollama",
            "Harbor.Storage.Jsonl",
            "Harbor.Storage.Memory",
            "Harbor.Storage.Sqlite",
            "Harbor.Tools.Builtin",
            "Harbor.Cli"
        ];
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, forbidden);
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Every Harbor.Providers.* (Infrastructure) project may reference
    ///     Harbor.Abstractions only — NOT Harbor.Core, NOT other Providers.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ProviderAssemblies))]
    public async Task Providers_ReferencesOnlyAbstractions(Assembly asm)
    {
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(
            asm,
            NonDomainNonSelfHarborAssemblies
                .Concat(new[]
                {
                    "Harbor.Terminal.Abstractions",
                    "Harbor.Providers.OpenAiCompatible",
                    "Harbor.Providers.Anthropic",
                    "Harbor.Providers.OpenAI",
                    "Harbor.Providers.Ollama",
                    "Harbor.Storage.Jsonl",
                    "Harbor.Storage.Memory",
                    "Harbor.Storage.Sqlite",
                    "Harbor.Tools.Builtin"
                })
                .Where(n => n != asm.GetName().Name)
                .ToArray());
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Every Harbor.Storage.* (Infrastructure) project may reference
    ///     Harbor.Abstractions only — NOT Harbor.Core, NOT other Storage.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(StorageAssemblies))]
    public async Task Storage_ReferencesOnlyAbstractions(Assembly asm)
    {
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(
            asm,
            NonDomainNonSelfHarborAssemblies
                .Concat(new[]
                {
                    "Harbor.Terminal.Abstractions",
                    "Harbor.Providers.OpenAiCompatible",
                    "Harbor.Providers.Anthropic",
                    "Harbor.Providers.OpenAI",
                    "Harbor.Providers.Ollama",
                    "Harbor.Storage.Jsonl",
                    "Harbor.Storage.Memory",
                    "Harbor.Storage.Sqlite",
                    "Harbor.Tools.Builtin"
                })
                .Where(n => n != asm.GetName().Name)
                .ToArray());
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Harbor.Tools.Builtin (Infrastructure) may reference Harbor.Abstractions
    ///     only — NOT Harbor.Core.
    /// </summary>
    [Test]
    public async Task ToolsBuiltin_ReferencesOnlyAbstractions()
    {
        var asm = typeof(ReadTool).Assembly;
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(
            asm,
            NonDomainNonSelfHarborAssemblies
                .Concat(new[]
                {
                    "Harbor.Terminal.Abstractions",
                    "Harbor.Providers.OpenAiCompatible",
                    "Harbor.Providers.Anthropic",
                    "Harbor.Providers.OpenAI",
                    "Harbor.Providers.Ollama",
                    "Harbor.Storage.Jsonl",
                    "Harbor.Storage.Memory",
                    "Harbor.Storage.Sqlite"
                })
                .ToArray());
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Every concrete Harbor.Tui.* renderer (Presentation) may reference
    ///     Harbor.Abstractions + Harbor.Terminal.Abstractions only — NOT Harbor.Core,
    ///     NOT Harbor.Application / Harbor.Registries, NOT Infrastructure.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(TuiRendererAssemblies))]
    public async Task TuiRenderers_ReferencesOnlyAbstractionsAndTuiAbstractions(Assembly asm)
    {
        string[] forbidden =
        [
            "Harbor.Core",
            "Harbor.Application",
            "Harbor.Registries",
            "Harbor.Plugins.Runtime",
            "Harbor.Scripting",
            "Harbor.Providers.OpenAiCompatible",
            "Harbor.Providers.Anthropic",
            "Harbor.Providers.OpenAI",
            "Harbor.Providers.Ollama",
            "Harbor.Storage.Jsonl",
            "Harbor.Storage.Memory",
            "Harbor.Storage.Sqlite",
            "Harbor.Tools.Builtin",
            "Harbor.Cli"
        ];
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, forbidden);
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Sanity check: the test project itself loads every Harbor assembly —
    ///     if any are missing, the per-assembly tests would silently skip. This
    ///     test fails loudly if the count drops below the expected baseline.
    /// </summary>
    [Test]
    public async Task AllExpectedHarborAssembliesAreLoaded()
    {
        // Sanity probe: touch a type from Harbor.Plugins.Runtime so its assembly
        // is force-loaded into the AppDomain. (It is a leaf-level project
        // referenced transitively by the test csproj but not touched by any
        // typeof() probe in the per-assembly tests above; without this nudge
        // it may not yet be loaded when this sanity check runs.)
        _ = typeof(CompiledPlugin).Assembly.GetName().Name;
        // Harbor.Core is now an empty facade (no types of its own — it forwards
        // to Harbor.Application + Harbor.Registries). Touch its FacadeMarker
        // type explicitly so the assembly is loaded into the AppDomain and
        // shows up in the inventory below.
        _ = typeof(FacadeMarker).Assembly.GetName().Name;
        // Harbor.Tools.Builtin is similarly an empty facade after the S2 split —
        // it forwards to the 14 Harbor.Tools.<Name> leaf projects. Touch its
        // FacadeMarker so it shows up in the inventory below.
        _ = typeof(Tools.Builtin.FacadeMarker).Assembly.GetName().Name;

        var loaded = ArchitectureTestHelpers.LoadHarborAssemblies();
        // Update this list when adding a new Harbor project. The test exists to
        // catch a regression where a ProjectReference is accidentally removed
        // from this test project (which would silently make the per-assembly
        // tests skip rather than fail).
        string[] expected =
        [
            // Harbor.Domain deleted in the F1 decoupling — its content lives in
            // Harbor.Abstractions.Contracts now (see AbstractionsSplitLayerRules).
            "Harbor.Abstractions.Contracts",
            "Harbor.Extensions",
            "Harbor.Abstractions",
            "Harbor.Terminal.Abstractions",
            "Harbor.Application",
            "Harbor.Registries",
            "Harbor.Core",
            "Harbor.Plugins.Runtime",
            // Harbor.Scripting moved to contrib/scripting (sprint 2) — outside
            // the main solution's layer enforcement scope.
            "Harbor.Providers.OpenAiCompatible",
            "Harbor.Providers.Anthropic",
            "Harbor.Providers.OpenAI",
            "Harbor.Providers.Ollama",
            "Harbor.Storage.Jsonl",
            "Harbor.Storage.Memory",
            "Harbor.Storage.Sqlite",
            "Harbor.Tools.Builtin",
            "Harbor.Tui.Ansi",
            "Harbor.Tui.Plain"
        ];
        var missing = expected.Where(n => !loaded.ContainsKey(n)).ToList();
        await Assert.That(missing).IsEmpty();
    }

    /// <summary>Provider assemblies to test, sourced as method data for TUnit.</summary>
    public static IEnumerable<Assembly> ProviderAssemblies()
    {
        yield return typeof(OpenAiCompatibleLlmClient).Assembly;
        yield return typeof(AnthropicLlmClient).Assembly;
        yield return typeof(OpenAILlmClient).Assembly;
        yield return typeof(OllamaLlmClient).Assembly;
    }

    /// <summary>Storage assemblies to test, sourced as method data for TUnit.</summary>
    public static IEnumerable<Assembly> StorageAssemblies()
    {
        yield return typeof(JsonlSessionStore).Assembly;
        yield return typeof(MemorySessionStore).Assembly;
        yield return typeof(SqliteSessionStore).Assembly;
    }

    /// <summary>TUI renderer assemblies to test, sourced as method data for TUnit.
    /// Only main-solution renderers are probed; contrib renderers (Spectre,
    /// Fullscreen, SpectreTui, TerminalGui, Termina, RazorConsole) moved out of
    /// scope in sprint 2.</summary>
    public static IEnumerable<Assembly> TuiRendererAssemblies()
    {
        yield return typeof(AnsiTuiRenderer).Assembly;
        yield return typeof(PlainTuiRenderer).Assembly;
    }
}
