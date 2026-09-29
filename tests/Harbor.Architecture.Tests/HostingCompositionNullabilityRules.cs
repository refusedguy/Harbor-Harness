// HostingCompositionNullabilityRules.cs — guard for the `null!` wave in the
// composition root (src/Harbor.Hosting), issue #562.
//
// The TEA state layer already has this gate
// (UiFrameworkNullabilityRules). It deliberately scoped itself out of the
// composition root, and named this file as the reason:
//
//     "The same pattern exists elsewhere in the repo (HarborCompositionContext,
//      IAgent.State) and those are owned by their own issues."
//
// #562 is that issue for HarborCompositionContext. Five public non-nullable
// properties were initialised with `null!` and filled in by whichever AddHarbor
// module happened to run first:
//
//     public IEventBus EventBus { get; internal set; } = null!;
//     public AgentRegistry Agents { get; internal set; } = null!;    // + Tools,
//     public ProviderRegistry Providers { get; internal set; } = null!;  //   Providers, Panels
//
// The C# `non-nullable` annotation is a contract the compiler checks in ONE
// file and not at all once the value arrives through DI. In the composition
// root that is not academic: a reordered module in Registration.AddHarbor did
// not fail, it read a member that was null — either as an NRE three frames
// later, or (StorageModule reading ctx.Registries.SessionStores) as a silently
// empty registry and a container that looked complete.
//
// The fix was structural — the four registries are constructor arguments of
// HarborRegistries now, and the two assigned-later members throw by name
// instead of returning null. This file stops the next contributor from putting
// a `null!` back into the composition root, where the compiler cannot help.
//
// WHAT IS ALLOWED: see SourceNullabilityScan, which owns the shared scanner
// and the reasoning behind `default!` and comment-stripping. The scope here is
// src/Harbor.Hosting; IAgent.State (the other site #562-era audits name) is a
// separate type in a different layer and is not in this slice.
//
// Runs as part of the build via the HarborArchitectureGate target in
// Directory.Build.props, so a re-introduced `null!` fails CI without anyone
// remembering to run this project.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Asserts that no file in the composition root suppresses a nullable
///     warning with the <c>null</c> literal plus the null-forgiving operator.
/// </summary>
public class HostingCompositionNullabilityRules
{
    /// <summary>The slice this gate covers — <c>src/&lt;name&gt;</c>.</summary>
    private const string Slice = "Harbor.Hosting";

    /// <summary>
    ///     The five members #562 removed, spelled out so the failure message
    ///     points at the history rather than only at the line.
    /// </summary>
    private const string WhatItCatches =
        "HarborCompositionContext.EventBus, HarborRegistries.Agents / Tools / Providers / Panels were the five "
        + "members behind this gate: all were `= null!` and all were non-null only because an earlier AddHarbor "
        + "module assigned them. A reordered module then read a null — as an NRE, or (StorageModule reading "
        + "ctx.Registries.SessionStores) as a silently empty registry. Assign such a member through a "
        + "set-once setter that throws by name, or take it as a constructor argument.";

    /// <summary>
    ///     No file in <c>src/Harbor.Hosting</c> initialises a field or property
    ///     with <c>null!</c>.
    /// </summary>
    [Test]
    public async Task Assert_NoNullForgivingNullInHostingCompositionRoot()
    {
        List<string> violations =
        [
            .. SourceNullabilityScan.FindNullForgivingNull(SourceNullabilityScan.EnumerateSources(Slice)),
        ];

        await Assert.That(violations).IsEmpty()
            .Because(
                "src/Harbor.Hosting is the composition root: a value assigned by one module and read by another "
                + "is not checked by the compiler, so a `null!` here is a wrong module order waiting to happen. "
                + $"{WhatItCatches} Violations: {(violations.Count == 0 ? "(none)" : string.Join(", ", violations))}");
    }

    /// <summary>
    ///     The slice is discoverable at all — i.e. <see cref="RepoPaths.RepoRoot" />
    ///     resolved and <c>src/Harbor.Hosting</c> exists.
    /// </summary>
    /// <remarks>
    ///     Without this, a broken repo-root discovery would make the gate above
    ///     vacuously green, which is the failure mode a ratchet has to guard
    ///     against. The floor is deliberately low (one file): the point is
    ///     that the scan RAN, not how much it covered.
    /// </remarks>
    [Test]
    public async Task Assert_HostingSliceIsDiscoverable()
    {
        IReadOnlyList<string> sources = SourceNullabilityScan.EnumerateSources(Slice);

        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("Harbor.slnx must sit above the test host, or the null-forgiving gate checks nothing.");
        await Assert.That(sources.Count).IsGreaterThan(0)
            .Because($"expected the composition root slice (src/{Slice}); found {sources.Count}.");
    }
}
