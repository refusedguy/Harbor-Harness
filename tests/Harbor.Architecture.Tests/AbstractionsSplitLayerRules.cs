// AbstractionsSplitLayerRules.cs — layer-dependency rules for the
// Harbor.Abstractions → Harbor.Domain + Harbor.Extensions + Harbor.Abstractions
// interfaces-only split. Task A1, round-6 architecture polish.
//
// The existing LayerDependencyTests.cs file covers the post-S1/S2 splits
// — Harbor.Core → Application+Registries; Harbor.Tools.Builtin → 14 leaves.
// This file adds the post-A1 split rules:
//
//   Harbor.Domain        → zero Harbor project references — pure domain models
//   Harbor.Extensions    → zero Harbor project references — pure BCL/NuGet
//                          pool helpers; consumers reference it DIRECTLY
//   Harbor.Abstractions  → references Harbor.Domain only — the facade;
//                          interfaces reference domain types. Pool helpers are
//                          NOT re-exported: direct consumers (Harbor.Application,
//                          Harbor.Tools.Builtin) add their own Harbor.Extensions
//                          reference
//
// Namespaces are preserved — Harbor.Abstractions.Models, .Events, .Permissions,
// .Models.Identifiers, .Extensions — so consumer code requires zero using
// changes; types are picked up transitively via the Harbor.Abstractions facade.
// These tests enforce that the dependency direction stays clean across future
// edits.

using Harbor.Abstractions.Extensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Tools;
namespace Harbor.Architecture.Tests;
/// <summary>
///     Layer-dependency rules for the Harbor.Abstractions god-project split
///     (Task A1). See file header for the canonical allowed/forbidden matrix.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a separate file:</b> the existing
///         <c>LayerDependencyTests.cs</c> covers the S1 (Harbor.Core →
///         Application + Registries) and S2 (Harbor.Tools.Builtin → 14 leaves)
///         splits. This file isolates the A1 split rules so a regression in
///         either layer doesn't accidentally disable the other's invariants.
///     </para>
///     <para>
///         <b>Namespaces preserved:</b> the moved files keep their original
///         <c>
///             namespace Harbor.Abstractions.{Models,Events,Permissions,
///             Models.Identifiers,Extensions};
///         </c>
///         declarations so consumer code
///         keeps compiling without <c>using</c> changes. The tests probe by
///         assembly (via <c>typeof(...).Assembly</c>), not by namespace.
///     </para>
/// </remarks>
public class AbstractionsSplitLayerRules
{
    // The full set of Harbor assemblies that Harbor.Domain must NOT reference.
    // Harbor.Domain is the pure domain layer — zero Harbor project references
    // are allowed. (Self-reference is structurally impossible.)
    private static readonly string[] NoHarborProjectRefs =
    [
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
    // must NOT reference. It may reference Harbor.Domain only (interfaces
    // reference domain types like Session, AgentMessage). Pool helpers
    // (Harbor.Extensions) are not re-exported — direct consumers reference
    // Harbor.Extensions themselves.
    private static readonly string[] AbstractionsForbiddenRefs =
    [
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
    ///     Harbor.Domain (pure domain models, value objects, events, permission
    ///     rules) must reference ZERO Harbor assemblies. Verifies the Domain
    ///     layer stays at the bottom of the dependency pyramid — any leak here
    ///     would re-create the god-project coupling that the A1 split removed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Probed via <c>typeof(Session).Assembly</c> — <c>Session</c> is
    ///         declared in <c>Harbor.Domain.dll</c> after the split (its
    ///         <c>namespace Harbor.Abstractions.Models;</c> declaration is
    ///         preserved so consumer code requires zero <c>using</c> changes).
    ///     </para>
    /// </remarks>
    [Test]
    public async Task Domain_HasZeroHarborProjectReferences()
    {
        var asm = typeof(Session).Assembly;
        await Assert.That(asm.GetName().Name).IsEqualTo("Harbor.Domain");
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
    ///         <c>Harbor.Extensions.dll</c> after the split (its
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
    ///     Harbor.Abstractions (the thin facade, interfaces-only after the A1
    ///     split) may reference Harbor.Domain only (interfaces reference domain
    ///     types like <see cref="AgentMessage" />, <see cref="Session" />) —
    ///     pool helpers are not re-exported.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Probed via <c>typeof(ITool).Assembly</c> — <c>ITool</c> stays
    ///         in <c>Harbor.Abstractions.dll</c> after the split (interfaces
    ///         are the facade's reason for existing).
    ///     </para>
    /// </remarks>
    [Test]
    public async Task Abstractions_ReferencesOnlyDomain()
    {
        var asm = typeof(ITool).Assembly;
        await Assert.That(asm.GetName().Name).IsEqualTo("Harbor.Abstractions");
        var violations = ArchitectureTestHelpers.FindForbiddenReferences(asm, AbstractionsForbiddenRefs);
        await Assert.That(violations).IsEmpty();
    }
}
