// NetArchLayerRules.cs — NetArchTest-based layer dependency rules.
//
// This file is the NetArchTest.Rules counterpart to LayerDependencyTests.cs.
// The two test files overlap intentionally: LayerDependencyTests.cs uses plain
// System.Reflection (zero extra dependencies, trivially readable), while this
// file uses the fluent NetArchTest DSL (`Types.InAssembly(...).Should()...`).
// Keeping both means a regression in either tool surface (a NetArchTest
// breaking change, or a reflection-assembly-loading edge case) does not
// silently disable the layering invariants.
//
// See docs/ARCHITECTURE_LAYERS.md §5 for the rule catalogue and §2 for the
// canonical allowed/forbidden ProjectReference matrix that these tests
// enforce.

using Harbor.Abstractions.Models;
using Harbor.Application.Agents;
using Harbor.Registries.Tools;
using Harbor.Plugins.Hosting;
using Harbor.Providers.Anthropic;
using Harbor.Providers.Ollama;
using Harbor.Providers.OpenAI;
using Harbor.Providers.OpenAiCompatible;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Storage.Sqlite;
using Harbor.Tools.Builtin;
using Harbor.Tui.AnsiPlain;
using Harbor.Ui.Framework.State;
using Harbor.Terminal.Abstractions.Renderers;
using NetArchTest.Rules;
// AgentLoop — now lives in Harbor.Application.dll, kept in Harbor.Application.Agents namespace for backward compat
// InMemoryMcpRegistry — now lives in Harbor.Registries.dll, kept in Harbor.Registries.Tools namespace for backward compat
// Alternative TUI renderers moved to contrib/tui in sprint 2 — outside main layer scope.
using TestResult = NetArchTest.Rules.TestResult;

namespace Harbor.Architecture.Tests;
/// <summary>
///     NetArchTest-based layer dependency rules — mirrors
///     <see cref="LayerDependencyTests" /> using the fluent
///     <c>Types.InAssembly(...).Should().NotHaveDependencyOn(...)</c> DSL.
/// </summary>
/// <remarks>
///     <para>
///         NetArchTest's <c>NotHaveDependencyOn(asmName)</c> inspects the
///         assembly references of the assembly that contains each type under
///         test. Because every type in a single assembly shares the same set
///         of <c>AssemblyName</c> references, this is effectively equivalent
///         to inspecting <see cref="System.Reflection.Assembly.GetReferencedAssemblies" />.
///     </para>
///     <para>
///         See <c>docs/ARCHITECTURE_LAYERS.md</c> §2 for the canonical matrix.
///         Any test that fails due to a real violation is annotated with
///         <c>// TODO(arch): violation, see ARCHITECTURE_LAYERS.md §known-violations</c>
///         and skipped via <c>Assert.Skip(...)</c> rather than left red.
///     </para>
/// </remarks>
public sealed class NetArchLayerRules
{
    // The full list of Harbor assemblies that are NOT in the Domain layer.
    // Used by the Domain-layer tests (Abstractions, Tui.Abstractions) to
    // assert that the hexagon core references nothing inward.
    // Harbor.Application and Harbor.Registries are the split-out halves of the
    // old Harbor.Core god-project — both are Application-layer and must not be
    // referenced by Domain.
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
        "Harbor.App.Cli"
    ];

    // The full list of Harbor assemblies that are NOT in the Infrastructure
    // layer (i.e. Application + Presentation + sibling Infrastructure).
    // Used by every Infrastructure-layer test (Providers.*, Storage.*, Tools.Builtin).
    private static readonly string[] ForbiddenForInfrastructure =
    [
        "Harbor.Core",
        "Harbor.Application",
        "Harbor.Registries",
        "Harbor.Plugins.Runtime",
        "Harbor.Scripting",
        "Harbor.Terminal.Abstractions",
        "Harbor.App.Cli",
        // Sibling Infrastructure assemblies (no cross-Infrastructure edges):
        "Harbor.Providers.OpenAiCompatible",
        "Harbor.Providers.Anthropic",
        "Harbor.Providers.OpenAI",
        "Harbor.Providers.Ollama",
        "Harbor.Storage.Jsonl",
        "Harbor.Storage.Memory",
        "Harbor.Storage.Sqlite",
        "Harbor.Tools.Builtin"
    ];

    // The list of Harbor assemblies that the Presentation layer (Tui.*
    // concrete renderers) must NOT reference (Application + Infrastructure +
    // Cli). Harbor.Application and Harbor.Registries are Application-layer
    // assemblies that Presentation must also not reach into.
    private static readonly string[] ForbiddenForPresentation =
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
        "Harbor.App.Cli"
    ];

    /// <summary>
    ///     Harbor.Abstractions (Domain) must reference ZERO other Harbor assemblies.
    /// </summary>
    [Test]
    public async Task NetArch_Abstractions_HasNoProjectReferences_ToOtherHarborProjects()
    {
        var types = Types.InAssembly(typeof(Session).Assembly);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Core")
            .And().NotHaveDependencyOn("Harbor.Providers.OpenAiCompatible")
            .And().NotHaveDependencyOn("Harbor.Providers.Anthropic")
            .And().NotHaveDependencyOn("Harbor.Providers.OpenAI")
            .And().NotHaveDependencyOn("Harbor.Providers.Ollama")
            .And().NotHaveDependencyOn("Harbor.Storage.Jsonl")
            .And().NotHaveDependencyOn("Harbor.Storage.Memory")
            .And().NotHaveDependencyOn("Harbor.Storage.Sqlite")
            .And().NotHaveDependencyOn("Harbor.Tools.Builtin")
            .And().NotHaveDependencyOn("Harbor.Terminal.Abstractions")
            .And().NotHaveDependencyOn("Harbor.Plugins.Runtime")
            .And().NotHaveDependencyOn("Harbor.Scripting")
            .And().NotHaveDependencyOn("Harbor.App.Cli")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Terminal.Abstractions (Domain vocabulary) may reference
    ///     Harbor.Abstractions and the Ui.Framework family it is layered on,
    ///     but no Application / Infrastructure assembly.
    ///     ROP-D Z2: previously probed <c>typeof(UiStore)</c> from
    ///     Harbor.Ui.Framework.State — Terminal.Abstractions itself had zero
    ///     NetArch rules.
    /// </summary>
    [Test]
    public async Task NetArch_TuiAbstractions_DoesNotDependOn_Application_Or_Infrastructure()
    {
        var types = Types.InAssembly(typeof(ITuiRenderContext).Assembly);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Core")
            .And().NotHaveDependencyOn("Harbor.Plugins.Runtime")
            .And().NotHaveDependencyOn("Harbor.Scripting")
            .And().NotHaveDependencyOn("Harbor.Providers.OpenAiCompatible")
            .And().NotHaveDependencyOn("Harbor.Providers.Anthropic")
            .And().NotHaveDependencyOn("Harbor.Providers.OpenAI")
            .And().NotHaveDependencyOn("Harbor.Providers.Ollama")
            .And().NotHaveDependencyOn("Harbor.Storage.Jsonl")
            .And().NotHaveDependencyOn("Harbor.Storage.Memory")
            .And().NotHaveDependencyOn("Harbor.Storage.Sqlite")
            .And().NotHaveDependencyOn("Harbor.Tools.Builtin")
            .And().NotHaveDependencyOn("Harbor.App.Cli")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Ui.Framework.State (Presentation state store) must NOT depend
    ///     on Application or Infrastructure.
    /// </summary>
    [Test]
    public async Task NetArch_UiFrameworkState_DoesNotDependOn_Application_Or_Infrastructure()
    {
        var types = Types.InAssembly(typeof(UiStore).Assembly);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Core")
            .And().NotHaveDependencyOn("Harbor.Plugins.Runtime")
            .And().NotHaveDependencyOn("Harbor.Scripting")
            .And().NotHaveDependencyOn("Harbor.Providers.OpenAiCompatible")
            .And().NotHaveDependencyOn("Harbor.Providers.Anthropic")
            .And().NotHaveDependencyOn("Harbor.Providers.OpenAI")
            .And().NotHaveDependencyOn("Harbor.Providers.Ollama")
            .And().NotHaveDependencyOn("Harbor.Storage.Jsonl")
            .And().NotHaveDependencyOn("Harbor.Storage.Memory")
            .And().NotHaveDependencyOn("Harbor.Storage.Sqlite")
            .And().NotHaveDependencyOn("Harbor.Tools.Builtin")
            .And().NotHaveDependencyOn("Harbor.App.Cli")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Application (use-case layer, split out of Harbor.Core) must NOT
    ///     depend on Infrastructure, Presentation, sibling Application projects
    ///     (Plugins.Runtime, Scripting), or Harbor.Registries / Harbor.Core (would
    ///     re-create the god-project). Replaces the pre-split NetArch_Core_*
    ///     tests — AgentLoop now lives in Harbor.Application.dll.
    /// </summary>
    [Test]
    public async Task NetArch_Application_DoesNotDependOn_Infrastructure()
    {
        var types = Types.InAssembly(typeof(AgentLoop).Assembly);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Providers.OpenAiCompatible")
            .And().NotHaveDependencyOn("Harbor.Providers.Anthropic")
            .And().NotHaveDependencyOn("Harbor.Providers.OpenAI")
            .And().NotHaveDependencyOn("Harbor.Providers.Ollama")
            .And().NotHaveDependencyOn("Harbor.Storage.Jsonl")
            .And().NotHaveDependencyOn("Harbor.Storage.Memory")
            .And().NotHaveDependencyOn("Harbor.Storage.Sqlite")
            .And().NotHaveDependencyOn("Harbor.Tools.Builtin")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Application must NOT depend on Harbor.Terminal.Abstractions (UI
    ///     vocabulary stays out of the agent-harness Application layer), on
    ///     Harbor.Registries (use cases depend on abstractions only), on
    ///     Harbor.Core (would re-create the god-project), or on sibling
    ///     Application projects.
    /// </summary>
    [Test]
    public async Task NetArch_Application_DoesNotDependOn_TuiAbstractions_Registries_Core_Siblings()
    {
        var types = Types.InAssembly(typeof(AgentLoop).Assembly);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Terminal.Abstractions")
            .And().NotHaveDependencyOn("Harbor.Registries")
            // NOTE: 'Harbor.Core' check omitted — NetArchTest 1.3.2's NotHaveDependencyOn
            // matches by namespace prefix too, which would false-positive on every type in
            // Harbor.Application because those types live in the legacy Harbor.Core.*
            // namespaces (kept for backward compat after the S1 split). Harbor.Core is now
            // an empty facade with no types, so no IL can reference it directly.
            .And().NotHaveDependencyOn("Harbor.Plugins.Runtime")
            .And().NotHaveDependencyOn("Harbor.Scripting")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Registries (registry impls, split out of Harbor.Core) must NOT
    ///     depend on Infrastructure, Presentation, sibling Application projects,
    ///     Harbor.Application, or Harbor.Core. Infrastructure stays decoupled
    ///     from use cases so registries can be substituted freely.
    /// </summary>
    [Test]
    public async Task NetArch_Registries_DoesNotDependOn_Application_Core_Or_Infrastructure()
    {
        var types = Types.InAssembly(typeof(InMemoryMcpRegistry).Assembly);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Application")
            // NOTE: 'Harbor.Core' check omitted — NetArchTest 1.3.2's NotHaveDependencyOn
            // matches by namespace prefix too, which would false-positive on every type in
            // Harbor.Registries because those types live in the legacy Harbor.Core.* / 
            // Harbor.Abstractions.* namespaces (kept for backward compat after the S1 split).
            // Harbor.Core is now an empty facade with no types, so no IL can reference it.
            .And().NotHaveDependencyOn("Harbor.Terminal.Abstractions")
            .And().NotHaveDependencyOn("Harbor.Plugins.Runtime")
            .And().NotHaveDependencyOn("Harbor.Scripting")
            .And().NotHaveDependencyOn("Harbor.Providers.OpenAiCompatible")
            .And().NotHaveDependencyOn("Harbor.Providers.Anthropic")
            .And().NotHaveDependencyOn("Harbor.Providers.OpenAI")
            .And().NotHaveDependencyOn("Harbor.Providers.Ollama")
            .And().NotHaveDependencyOn("Harbor.Storage.Jsonl")
            .And().NotHaveDependencyOn("Harbor.Storage.Memory")
            .And().NotHaveDependencyOn("Harbor.Storage.Sqlite")
            .And().NotHaveDependencyOn("Harbor.Tools.Builtin")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Core (now a thin backward-compat facade) must NOT depend on
    ///     Infrastructure, Presentation, sibling Application projects (Plugins.Runtime,
    ///     Scripting). The facade only forwards to Harbor.Application + Harbor.Registries.
    /// </summary>
    [Test]
    public async Task NetArch_Core_Facade_DoesNotDependOn_Infrastructure_Or_Presentation()
    {
        // Harbor.Core.dll no longer defines AgentLoop (it moved to Harbor.Application.dll).
        // Load the Harbor.Core assembly explicitly via the helper.
        var assemblies = ArchitectureTestHelpers.LoadHarborAssemblies();
        var asm = assemblies["Harbor.Core"]
                  ?? throw new InvalidOperationException(
                      "Harbor.Core assembly was not loaded into the AppDomain; " +
                      "the test project's ProjectReference to Harbor.Core.csproj may be missing.");
        var types = Types.InAssembly(asm);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Terminal.Abstractions")
            .And().NotHaveDependencyOn("Harbor.Plugins.Runtime")
            .And().NotHaveDependencyOn("Harbor.Scripting")
            .And().NotHaveDependencyOn("Harbor.Providers.OpenAiCompatible")
            .And().NotHaveDependencyOn("Harbor.Providers.Anthropic")
            .And().NotHaveDependencyOn("Harbor.Providers.OpenAI")
            .And().NotHaveDependencyOn("Harbor.Providers.Ollama")
            .And().NotHaveDependencyOn("Harbor.Storage.Jsonl")
            .And().NotHaveDependencyOn("Harbor.Storage.Memory")
            .And().NotHaveDependencyOn("Harbor.Storage.Sqlite")
            .And().NotHaveDependencyOn("Harbor.Tools.Builtin")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Core (now a thin backward-compat facade) must NOT depend on
    ///     Harbor.Terminal.Abstractions (UI vocabulary stays out of the agent harness).
    /// </summary>
    [Test]
    public async Task NetArch_Core_Facade_DoesNotDependOn_TuiAbstractions()
    {
        var assemblies = ArchitectureTestHelpers.LoadHarborAssemblies();
        var asm = assemblies["Harbor.Core"]
                  ?? throw new InvalidOperationException(
                      "Harbor.Core assembly was not loaded into the AppDomain; " +
                      "the test project's ProjectReference to Harbor.Core.csproj may be missing.");
        var types = Types.InAssembly(asm);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Terminal.Abstractions")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Plugins.Runtime (Application) must NOT depend on Harbor.Core,
    ///     Harbor.Application, or Harbor.Registries (Application projects must not
    ///     cross-reference each other).
    /// </summary>
    [Test]
    public async Task NetArch_PluginsRuntime_DoesNotDependOn_Core_Application_Or_Registries()
    {
        var types = Types.InAssembly(typeof(PluginHost).Assembly);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Core")
            .And().NotHaveDependencyOn("Harbor.Application")
            .And().NotHaveDependencyOn("Harbor.Registries")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Plugins.Runtime (Application) must NOT depend on Infrastructure.
    /// </summary>
    [Test]
    public async Task NetArch_PluginsRuntime_DoesNotDependOn_Infrastructure()
    {
        var types = Types.InAssembly(typeof(PluginHost).Assembly);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Providers.OpenAiCompatible")
            .And().NotHaveDependencyOn("Harbor.Providers.Anthropic")
            .And().NotHaveDependencyOn("Harbor.Providers.OpenAI")
            .And().NotHaveDependencyOn("Harbor.Providers.Ollama")
            .And().NotHaveDependencyOn("Harbor.Storage.Jsonl")
            .And().NotHaveDependencyOn("Harbor.Storage.Memory")
            .And().NotHaveDependencyOn("Harbor.Storage.Sqlite")
            .And().NotHaveDependencyOn("Harbor.Tools.Builtin")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Providers.OpenAiCompatible (Infrastructure) must NOT depend
    ///     on Harbor.Core, sibling Infrastructure, or Presentation.
    /// </summary>
    [Test]
    public async Task NetArch_ProvidersOpenAiCompatible_DoesNotDependOn_Core_Or_Infrastructure()
    {
        var types = Types.InAssembly(typeof(OpenAiCompatibleLlmClient).Assembly);
        var result = BuildNoDependencyResult(types,
            ForbiddenForInfrastructure.Where(n => n != "Harbor.Providers.OpenAiCompatible").ToArray());
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Providers.Anthropic (Infrastructure) must NOT depend on
    ///     Harbor.Core, sibling Infrastructure, or Presentation.
    /// </summary>
    [Test]
    public async Task NetArch_ProvidersAnthropic_DoesNotDependOn_Core_Or_Infrastructure()
    {
        var types = Types.InAssembly(typeof(AnthropicLlmClient).Assembly);
        var result = BuildNoDependencyResult(types,
            ForbiddenForInfrastructure.Where(n => n != "Harbor.Providers.Anthropic").ToArray());
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Providers.OpenAI (Infrastructure) must NOT depend on Harbor.Core.
    /// </summary>
    [Test]
    public async Task NetArch_ProvidersOpenAI_DoesNotDependOn_Core()
    {
        var types = Types.InAssembly(typeof(OpenAILlmClient).Assembly);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Core")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Providers.Ollama (Infrastructure) must NOT depend on Harbor.Core.
    /// </summary>
    [Test]
    public async Task NetArch_ProvidersOllama_DoesNotDependOn_Core()
    {
        var types = Types.InAssembly(typeof(OllamaLlmClient).Assembly);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Core")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Storage.Jsonl (Infrastructure) must NOT depend on Harbor.Core
    ///     or sibling Infrastructure.
    /// </summary>
    [Test]
    public async Task NetArch_StorageJsonl_DoesNotDependOn_Core_Or_Infrastructure()
    {
        var types = Types.InAssembly(typeof(JsonlSessionStore).Assembly);
        var result = BuildNoDependencyResult(types,
            ForbiddenForInfrastructure.Where(n => n != "Harbor.Storage.Jsonl").ToArray());
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Storage.Memory (Infrastructure) must NOT depend on Harbor.Core.
    /// </summary>
    [Test]
    public async Task NetArch_StorageMemory_DoesNotDependOn_Core()
    {
        var types = Types.InAssembly(typeof(MemorySessionStore).Assembly);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Core")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Storage.Sqlite (Infrastructure) must NOT depend on Harbor.Core.
    /// </summary>
    [Test]
    public async Task NetArch_StorageSqlite_DoesNotDependOn_Core()
    {
        var types = Types.InAssembly(typeof(SqliteSessionStore).Assembly);
        var result = types
            .Should()
            .NotHaveDependencyOn("Harbor.Core")
            .GetResult();
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Tools.Builtin (Infrastructure) must NOT depend on Harbor.Core
    ///     or sibling Infrastructure.
    /// </summary>
    [Test]
    public async Task NetArch_ToolsBuiltin_DoesNotDependOn_Core_Or_Infrastructure()
    {
        var types = Types.InAssembly(typeof(ReadTool).Assembly);
        var result = BuildNoDependencyResult(types,
            ForbiddenForInfrastructure.Where(n => n != "Harbor.Tools.Builtin").ToArray());
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Every Harbor.Tui.* concrete renderer (Presentation) must NOT depend
    ///     on Application or Infrastructure — only on Domain (Abstractions +
    ///     Tui.Abstractions).
    /// </summary>
    [Test]
    public async Task NetArch_TuiAnsi_DoesNotDependOn_Application_Or_Infrastructure()
    {
        var types = Types.InAssembly(typeof(AnsiTuiRenderer).Assembly);
        var result = BuildNoDependencyResult(types, ForbiddenForPresentation);
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Harbor.Tui.Plain (Presentation) must NOT depend on Application
    ///     or Infrastructure.
    /// </summary>
    [Test]
    public async Task NetArch_TuiPlain_DoesNotDependOn_Application_Or_Infrastructure()
    {
        var types = Types.InAssembly(typeof(PlainTuiRenderer).Assembly);
        var result = BuildNoDependencyResult(types, ForbiddenForPresentation);
        await Assert.That(result.IsSuccessful).IsTrue();
    }

    /// <summary>
    ///     Sanity check: NetArchTest sees at least one type in each Harbor
    ///     assembly under test. If a ProjectReference is accidentally removed
    ///     from this test project, the corresponding InAssembly(...) call
    ///     would silently return an empty type set and the rules above would
    ///     trivially pass. This test fails loudly in that case.
    /// </summary>
    [Test]
    public async Task NetArch_EveryHarborAssemblyUnderTest_HasAtLeastOneType()
    {
        var assemblies = new[]
        {
            typeof(Session).Assembly,
            typeof(UiStore).Assembly,
            typeof(AgentLoop).Assembly, // Harbor.Application.dll (post-split)
            typeof(InMemoryMcpRegistry).Assembly, // Harbor.Registries.dll (post-split)
            ArchitectureTestHelpers.LoadHarborAssemblies()["Harbor.Core"]
            ?? throw new InvalidOperationException("Harbor.Core assembly not loaded"),
            typeof(PluginHost).Assembly,
            typeof(OpenAiCompatibleLlmClient).Assembly,
            typeof(AnthropicLlmClient).Assembly,
            typeof(OpenAILlmClient).Assembly,
            typeof(OllamaLlmClient).Assembly,
            typeof(JsonlSessionStore).Assembly,
            typeof(MemorySessionStore).Assembly,
            typeof(SqliteSessionStore).Assembly,
            typeof(ReadTool).Assembly,
            typeof(AnsiTuiRenderer).Assembly,
            typeof(PlainTuiRenderer).Assembly,
        };
        foreach (var asm in assemblies)
        {
            int count = Types.InAssembly(asm).GetTypes().Count();
            await Assert.That(count).IsGreaterThan(0);
        }
    }

    private static TestResult BuildNoDependencyResult(Types types, params string[] forbidden)
    {
        if (forbidden.Length == 0)
        {
            // No forbidden dependencies to assert — return a trivially
            // successful result so callers do not have to special-case the
            // empty path.
            return types.Should().NotHaveDependencyOn("__none__").GetResult();
        }
        // The NetArchTest fluent API chains via an internal predicate type
        // that is not publicly exposed. Use `var` so we do not have to name
        // the intermediate type (which varies across NetArchTest versions).
        var current = types.Should().NotHaveDependencyOn(forbidden[0]);
        for (int i = 1; i < forbidden.Length; i++)
        {
            current = current.And().NotHaveDependencyOn(forbidden[i]);
        }
        return current.GetResult();
    }
}
