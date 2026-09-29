// UiFrameworkNullabilityRules.cs — guard for the `null!` wave in src/Harbor.Ui.Framework*.
//
// The TEA state layer is 0/157 files on Result, and that is correct: a reducer
// cannot fail, so a Result in a transition would be a lie about an impossibility.
// What the layer was NOT free of was the other nullable lie — a member declared
// non-nullable that is initialised with the null-forgiving operator, so the
// compiler is asked to accept a value nobody assigned.
//
// Six of those lived in one class, ProjectionCache in
// src/Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs. They are
// now `required`, which makes the compiler state the invariant instead of a
// comment. This file stops the next contributor from putting one back.
//
// SCOPE. Deliberately src/Harbor.Ui.Framework* only. The composition root
// (src/Harbor.Hosting, HarborCompositionContext) carried the same five members
// and is gated separately by HostingCompositionNullabilityRules (#562).
// IAgent.State was the third such lie; #559 removed it by typing the property as
// Maybe<AgentState>, and AgentStateContractRules guards that contract. Widening
// THIS gate would make it red on a tree this PR is not changing.
//
// WHAT IS ALLOWED, and why — recorded once in SourceNullabilityScan, which now
// owns the shared scanner: `default!` is a different idiom with a legitimate
// use (StoreSubscriberViewModel.Selector<T>._last holds a default(T) sentinel
// behind a flag), and the literal `null!` inside a comment must not fail the
// gate.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Asserts that no file under <c>src/Harbor.Ui.Framework*/</c> suppresses a
///     nullable warning with the <c>null</c> literal plus the null-forgiving
///     operator.
/// </summary>
/// <remarks>
///     <para>
///         The rule is a source scan rather than a reflection test because
///         <c>null!</c> compiles away: by the time an assembly is loaded there is
///         nothing left to inspect, so the only way to see the operator is the
///         text. It therefore reuses <see cref="RepoPaths" />, the same
///         repo-root discovery the README gate uses, and degrades to "nothing to
///         check" when the marker file is absent (a published test host).
///     </para>
///     <para>
///         Runs as part of the build via the HarborArchitectureGate target in
///         Directory.Build.props, so a re-introduced <c>null!</c> fails CI without
///         anyone remembering to run this project.
///     </para>
/// </remarks>
public class UiFrameworkNullabilityRules
{
    /// <summary>Every <c>.cs</c> file under the TEA state layer, sorted for a stable failure message.</summary>
    private static IReadOnlyList<string> EnumerateUiFrameworkSources() =>
        SourceNullabilityScan.EnumerateSources("Harbor.Ui.Framework*");

    /// <summary>
    ///     No file under <c>src/Harbor.Ui.Framework*/</c> initialises a field or
    ///     property with <c>null!</c>.
    /// </summary>
    /// <remarks>
    ///     Declared <c>required</c> when the member is genuinely always assigned —
    ///     that is what this file removed. Use <c>Maybe&lt;T&gt;</c> when absence is
    ///     real and the consumer must branch, and a plain <c>T?</c> when there is a
    ///     working default. Only a member that is assigned exactly once, at a
    ///     construction site the compiler can see, belongs in <c>required</c>.
    /// </remarks>
    [Test]
    public async Task Assert_NoNullForgivingNullInUiFramework()
    {
        IReadOnlyList<string> violations =
            SourceNullabilityScan.FindNullForgivingNull(EnumerateUiFrameworkSources());

        await Assert.That(violations).IsEmpty()
            .Because(
                "src/Harbor.Ui.Framework* must not suppress nullable warnings with `null!`. "
                + "Use `required` when the member is always assigned at a visible construction site, "
                + "`Maybe<T>` when absence is real and the consumer must branch, or `T?` when there is "
                + "a working default. See ProjectionCache in DefaultUiProjector.cs for the worked example. "
                + $"Violations: {(violations.Count == 0 ? "(none)" : string.Join(", ", violations))}");
    }

    /// <summary>
    ///     The slice is discoverable at all — i.e. <see cref="RepoPaths.RepoRoot" />
    ///     resolved and at least one <c>src/Harbor.Ui.Framework*</c> directory exists.
    /// </summary>
    /// <remarks>
    ///     Without this, a broken repo-root discovery would make the gate above
    ///     vacuously green, which is the failure mode a ratchet has to guard
    ///     against.
    /// </remarks>
    [Test]
    public async Task Assert_UiFrameworkSliceIsDiscoverable()
    {
        var sources = EnumerateUiFrameworkSources();

        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("Harbor.slnx must sit above the test host, or the null-forgiving gate checks nothing.");
        await Assert.That(sources.Count).IsGreaterThan(100)
            .Because($"expected the full TEA slice (9 projects, >100 files); found {sources.Count}.");
    }
}
