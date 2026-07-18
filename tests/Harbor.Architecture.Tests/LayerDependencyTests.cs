using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions.State;
using Harbor.Core.Agents;
using Harbor.Plugins.Runtime.Hosting;
using Harbor.Scripting.Bridge;
using Harbor.Providers.OpenAiCompatible;
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
using Harbor.Providers.Ollama;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Storage.Sqlite;
using Harbor.Tools.Builtin;
using Harbor.Tui.Ansi;
using Harbor.Tui.Plain;
using Harbor.Tui.Spectre;
using Harbor.Tui.Spectre.Fullscreen;
using Harbor.Tui.TerminalGui;
using Harbor.Tui.Termina;
using Harbor.Tui.RazorConsole;
// Use a using-alias to disambiguate the two SpectreTuiRenderer types
// (Harbor.Tui.Spectre.SpectreTuiRenderer vs Harbor.Tui.SpectreTui.SpectreTuiRenderer).
using SpectreTuiProjectRenderer = Harbor.Tui.SpectreTui.SpectreTuiRenderer;

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
    // layer (Harbor.Abstractions, Harbor.Tui.Abstractions) must NOT reference.
    private static readonly string[] NonDomainHarborAssemblies =
    [
        "Harbor.Core",
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
        "Harbor.Cli",
    ];

    // The set of Harbor assemblies that Infrastructure projects must NOT reference.
    // (Infrastructure → Domain only; never Application, never other Infrastructure,
    // never Presentation.)
    private static readonly string[] NonDomainNonSelfHarborAssemblies =
    [
        "Harbor.Core",
        "Harbor.Plugins.Runtime",
        "Harbor.Scripting",
        "Harbor.Cli",
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
    ///     Harbor.Tui.Abstractions (Domain) may reference Harbor.Abstractions but
    ///     nothing else from Harbor.
    /// </summary>
    [Test]
    public async Task TuiAbstractions_ReferencesOnlyAbstractions()
    {
        var asm = typeof(UiStore).Assembly;
        var forbidden = NonDomainHarborAssemblies
            .Where(n => n != "Harbor.Abstractions")
            .ToArray();
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, forbidden);
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Harbor.Core (Application) may reference Harbor.Abstractions but NOT
    ///     Harbor.Tui.Abstractions (TUI vocabulary stays out of the agent harness)
    ///     and NOT any Infrastructure / Presentation project.
    /// </summary>
    [Test]
    public async Task Core_ReferencesOnlyAbstractions()
    {
        var asm = typeof(AgentLoop).Assembly;
        string[] forbidden =
        [
            "Harbor.Tui.Abstractions",
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
            "Harbor.Cli",
        ];
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, forbidden);
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Harbor.Plugins.Runtime (Application) may reference Harbor.Abstractions
    ///     and Harbor.Tui.Abstractions (it needs ITuiPlugin for plugin-contributed
    ///     panels) but NOT Harbor.Core (Application must not cross-reference), NOT
    ///     Harbor.Scripting, NOT Infrastructure, NOT Presentation.
    /// </summary>
    [Test]
    public async Task PluginsRuntime_ReferencesOnlyAbstractions()
    {
        var asm = typeof(PluginHost).Assembly;
        string[] forbidden =
        [
            "Harbor.Core",
            "Harbor.Scripting",
            "Harbor.Providers.OpenAiCompatible",
            "Harbor.Providers.Anthropic",
            "Harbor.Providers.OpenAI",
            "Harbor.Providers.Ollama",
            "Harbor.Storage.Jsonl",
            "Harbor.Storage.Memory",
            "Harbor.Storage.Sqlite",
            "Harbor.Tools.Builtin",
            "Harbor.Cli",
        ];
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, forbidden);
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Harbor.Scripting (Application) may reference Harbor.Abstractions only.
    /// </summary>
    [Test]
    public async Task Scripting_ReferencesOnlyAbstractions()
    {
        var asm = typeof(ScriptGlobals).Assembly;
        string[] forbidden =
        [
            "Harbor.Core",
            "Harbor.Tui.Abstractions",
            "Harbor.Plugins.Runtime",
            "Harbor.Providers.OpenAiCompatible",
            "Harbor.Providers.Anthropic",
            "Harbor.Providers.OpenAI",
            "Harbor.Providers.Ollama",
            "Harbor.Storage.Jsonl",
            "Harbor.Storage.Memory",
            "Harbor.Storage.Sqlite",
            "Harbor.Tools.Builtin",
            "Harbor.Cli",
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
                    "Harbor.Tui.Abstractions",
                    "Harbor.Providers.OpenAiCompatible",
                    "Harbor.Providers.Anthropic",
                    "Harbor.Providers.OpenAI",
                    "Harbor.Providers.Ollama",
                    "Harbor.Storage.Jsonl",
                    "Harbor.Storage.Memory",
                    "Harbor.Storage.Sqlite",
                    "Harbor.Tools.Builtin",
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
                    "Harbor.Tui.Abstractions",
                    "Harbor.Providers.OpenAiCompatible",
                    "Harbor.Providers.Anthropic",
                    "Harbor.Providers.OpenAI",
                    "Harbor.Providers.Ollama",
                    "Harbor.Storage.Jsonl",
                    "Harbor.Storage.Memory",
                    "Harbor.Storage.Sqlite",
                    "Harbor.Tools.Builtin",
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
                    "Harbor.Tui.Abstractions",
                    "Harbor.Providers.OpenAiCompatible",
                    "Harbor.Providers.Anthropic",
                    "Harbor.Providers.OpenAI",
                    "Harbor.Providers.Ollama",
                    "Harbor.Storage.Jsonl",
                    "Harbor.Storage.Memory",
                    "Harbor.Storage.Sqlite",
                })
                .ToArray());
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Every concrete Harbor.Tui.* renderer (Presentation) may reference
    ///     Harbor.Abstractions + Harbor.Tui.Abstractions only — NOT Harbor.Core,
    ///     NOT Infrastructure.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(TuiRendererAssemblies))]
    public async Task TuiRenderers_ReferencesOnlyAbstractionsAndTuiAbstractions(Assembly asm)
    {
        string[] forbidden =
        [
            "Harbor.Core",
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
            "Harbor.Cli",
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
        var loaded = ArchitectureTestHelpers.LoadHarborAssemblies();
        // Update this list when adding a new Harbor project. The test exists to
        // catch a regression where a ProjectReference is accidentally removed
        // from this test project (which would silently make the per-assembly
        // tests skip rather than fail).
        string[] expected =
        [
            "Harbor.Abstractions",
            "Harbor.Tui.Abstractions",
            "Harbor.Core",
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
            "Harbor.Tui.Ansi",
            "Harbor.Tui.Plain",
            "Harbor.Tui.Spectre",
            "Harbor.Tui.Spectre.Fullscreen",
            "Harbor.Tui.SpectreTui",
            "Harbor.Tui.TerminalGui",
            "Harbor.Tui.Termina",
            "Harbor.Tui.RazorConsole",
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

    /// <summary>TUI renderer assemblies to test, sourced as method data for TUnit.</summary>
    public static IEnumerable<System.Reflection.Assembly> TuiRendererAssemblies()
    {
        yield return typeof(AnsiTuiRenderer).Assembly;
        yield return typeof(PlainTuiRenderer).Assembly;
        yield return typeof(SpectreTuiRenderer).Assembly;
        yield return typeof(FullscreenTuiRenderer).Assembly;
        yield return typeof(SpectreTuiProjectRenderer).Assembly;
        yield return typeof(TerminalGuiRenderer).Assembly;
        yield return typeof(TerminaRenderer).Assembly;
        yield return typeof(RazorConsoleRenderer).Assembly;
    }
}
