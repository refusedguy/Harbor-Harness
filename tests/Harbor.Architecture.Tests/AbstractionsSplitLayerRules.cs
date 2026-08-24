// AbstractionsSplitLayerRules.cs — layer-dependency rules for the
// Harbor.Abstractions contract split (F1 decoupling, sprint 5v2).
//
// History: the round-6 A1 split created Harbor.Domain + Harbor.Extensions with
// Harbor.Abstractions as a transitive facade. Sprint 5v2 F1 completed the
// decoupling ("no compromises"):
//
//   Harbor.Abstractions.Contracts -> zero Harbor project references — pure
//                                    contract models (former Harbor.Domain,
//                                    project deleted)
//   Harbor.Extensions             -> zero Harbor project references — pure
//                                    BCL/NuGet pool helpers; consumers reference
//                                    it DIRECTLY
//   Harbor.Abstractions           -> references Harbor.Abstractions.Contracts
//                                    only — the facade; interfaces reference
//                                    contract types. Pool helpers are NOT
//                                    re-exported.
//
// Namespaces are preserved — Harbor.Abstractions.Models, .Events, .Permissions,
// .Models.Identifiers, .Extensions — so consumer code requires zero using
// changes; contract types are picked up transitively via the Harbor.Abstractions
// facade. These tests enforce that the dependency direction stays clean across
// future edits. See docs/adr/ADR-007 for the decision record.

using Harbor.Abstractions.Extensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Tools;
namespace Harbor.Architecture.Tests;
/// <summary>
///     Layer-dependency rules for the Harbor.Abstractions contract split.
///     See file header for the canonical allowed/forbidden matrix and
///     docs/adr/ADR-007 for the decision record.
/// </summary>
/// <remarks>
///     <para>
///         The tests probe by assembly (via <c>typeof(...).Assembly</c>), not by
///         namespace: the moved files keep their original
///         <c>namespace Harbor.Abstractions.{Models,Events,Permissions,
///         Models.Identifiers,Extensions};</c> declarations so consumer code
///         keeps compiling without <c>using</c> changes.
///     </para>
/// </remarks>
public class AbstractionsSplitLayerRules
{
    // The full set of Harbor assemblies that Harbor.Abstractions.Contracts must
    // NOT reference. The contract layer is the bottom of the pyramid — zero
    // Harbor project references are allowed.
    private static readonly string[] NoHarborProjectRefs =
    [
        "Harbor.Domain",
        "Harbor.Extensions",
        "Harbor.Abstractions",
        "Harbor.Terminal.Abstractions",
        "Harbor.Core",
        "Harbor.Application",
        "Harbor.Registries",
        "Harbor.Plugins.Runtime",
        "Harbor.Plugins.Abstractions",
        "Harbor.Plugins.Storage",
        "Harbor.Plugins.Compilation",
        "Harbor.Plugins.Instantiation",
        "Harbor.Plugins.Registration",
        "Harbor.Plugins.Hosting",
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

    // The set of Harbor assemblies that Harbor.Extensions must NOT reference.
    // Harbor.Extensions is a pure BCL/NuGet helper layer (ArrayPool,
    // StringBuilderPool, FrozenSet materializers, generic MemoryPack
    // round-trips) — zero Harbor project references are allowed. Consumers
    // that use the helpers reference Harbor.Extensions directly.
    private static readonly string[] ExtensionsForbiddenRefs =
    [
        "Harbor.Abstractions.Contracts",
        "Harbor.Domain",
        "Harbor.Abstractions",
        "Harbor.Terminal.Abstractions",
        "Harbor.Core",
        "Harbor.Application",
        "Harbor.Registries",
        "Harbor.Plugins.Runtime",
        "Harbor.Plugins.Abstractions",
        "Harbor.Plugins.Storage",
        "Harbor.Plugins.Compilation",
        "Harbor.Plugins.Instantiation",
        "Harbor.Plugins.Registration",
        "Harbor.Plugins.Hosting",
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

    // The set of Harbor assemblies that Harbor.Abstractions (the thin facade)
    // must NOT reference. It may reference Harbor.Abstractions.Contracts only
    // (interfaces reference contract types like Session, AgentMessage). Pool
    // helpers (Harbor.Extensions) are not re-exported — direct consumers
    // reference Harbor.Extensions themselves.
    private static readonly string[] AbstractionsForbiddenRefs =
    [
        "Harbor.Domain",
        "Harbor.Extensions",
        "Harbor.Terminal.Abstractions",
        "Harbor.Core",
        "Harbor.Application",
        "Harbor.Registries",
        "Harbor.Plugins.Runtime",
        "Harbor.Plugins.Abstractions",
        "Harbor.Plugins.Storage",
        "Harbor.Plugins.Compilation",
        "Harbor.Plugins.Instantiation",
        "Harbor.Plugins.Registration",
        "Harbor.Plugins.Hosting",
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

    /// <summary>
    ///     Harbor.Abstractions.Contracts (pure contract models, value objects,
    ///     events, permission rules — the former Harbor.Domain) must reference
    ///     ZERO Harbor assemblies. Verifies the contract layer stays at the
    ///     bottom of the dependency pyramid.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Probed via <c>typeof(Session).Assembly</c> — <c>Session</c> is
    ///         declared in <c>Harbor.Abstractions.Contracts.dll</c> after the
    ///         F1 decoupling (its <c>namespace Harbor.Abstractions.Models;</c>
    ///         declaration is preserved so consumer code requires zero
    ///         <c>using</c> changes).
    ///     </para>
    /// </remarks>
    [Test]
    public async Task Contracts_HasZeroHarborProjectReferences()
    {
        var asm = typeof(Session).Assembly;
        await Assert.That(asm.GetName().Name).IsEqualTo("Harbor.Abstractions.Contracts");
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, NoHarborProjectRefs);
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Harbor.Extensions (infrastructure helpers: ArrayPool, StringBuilder
    ///     pool, FrozenSet materializers, MemoryPack round-trip helpers) must
    ///     reference ZERO Harbor assemblies — it is a pure BCL/NuGet helper
    ///     layer. Consumers that use the helpers reference it directly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Probed via <c>typeof(StringBuilderPool).Assembly</c> —
    ///         <c>StringBuilderPool</c> is declared in
    ///         <c>Harbor.Extensions.dll</c> (its
    ///         <c>namespace Harbor.Abstractions.Extensions;</c> declaration is
    ///         preserved so consumer code requires zero <c>using</c> changes).
    ///     </para>
    /// </remarks>
    [Test]
    public async Task Extensions_HasZeroHarborProjectReferences()
    {
        var asm = typeof(StringBuilderPool).Assembly;
        await Assert.That(asm.GetName().Name).IsEqualTo("Harbor.Extensions");
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, ExtensionsForbiddenRefs);
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Harbor.Abstractions (the thin facade, interfaces-only) may reference
    ///     Harbor.Abstractions.Contracts only (interfaces reference contract
    ///     types like <see cref="AgentMessage" />, <see cref="Session" />) —
    ///     pool helpers are not re-exported.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Probed via <c>typeof(ITool).Assembly</c> — <c>ITool</c> stays
    ///         in <c>Harbor.Abstractions.dll</c> (interfaces are the facade's
    ///         reason for existing).
    ///     </para>
    /// </remarks>
    [Test]
    public async Task Abstractions_ReferencesOnlyContracts()
    {
        var asm = typeof(ITool).Assembly;
        await Assert.That(asm.GetName().Name).IsEqualTo("Harbor.Abstractions");
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, AbstractionsForbiddenRefs);
        await Assert.That(violations).IsEmpty();
    }
}
