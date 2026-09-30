using Harbor.Abstractions.Events;

namespace Harbor.Terminal.Abstractions.Renderers;

/// <summary>
///     The one sentence a failed compaction owes the user, in one place (issue #840).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> <see cref="CompactionFailedEvent"/>
///         is published by <c>CompactionBehavior.PublishFailureAsync</c>, which then
///         returns <c>TruncationFallback: true</c> — the run CONTINUES on a shortened
///         history, and the same method's comment calls the fallback irreversible. A
///         failure that leaves no mark is therefore not a quieter failure; it is a
///         session that silently lost context and a status bar that never says so.
///     </para>
///     <para>
///         <b>Why the wording lives here and not in one renderer.</b> #773 fixed the
///         status cell and put this sentence in
///         <c>ChatAppReducer.OnCompactionFailed</c>. The four renderers that own their
///         own compaction output would each have retyped it — and the two console
///         handlers are already near-copies of one another, so a divergence between
///         them would be invisible until somebody compared two transcripts. This is
///         the surface <c>docs/ARCHITECTURE_LAYERS.md</c> §3 names for renderer-shared
///         vocabulary ("Presentation projects must NOT reference each other … They may
///         share the <c>Harbor.Terminal.Abstractions</c> contract surface").
///     </para>
///     <para>
///         <b>What this does not unify.</b> Only the failure line. The started and
///         completed lines are genuinely per-renderer — AnsiPlain brackets them and
///         prints a duration, NickConsoleEx uses markup, CellForge writes
///         <c>"history compacted"</c> — and folding those together would be a
///         visual-regression change to three surfaces, which is not this issue.
///     </para>
///     <para>
///         <b>The known second copy.</b> <c>ChatAppReducer.OnCompactionFailed</c> still
///         holds its own literal, and it has to:
///         <c>Harbor.Ui.Framework.State</c> sits <i>below</i> this assembly and cannot
///         reference it without a cycle. Two copies by construction of the layering,
///         not five. <c>CompactionLifecycleLineTests</c> asserts the literal on both
///         channels, which is what keeps the two from drifting.
///     </para>
/// </remarks>
public static class CompactionLifecycleLines
{
    /// <summary>
    ///     The truncation warning a renderer shows when compaction failed and the
    ///     agent loop fell back to tail truncation.
    /// </summary>
    /// <param name="error">
    ///     The summarizer's own failure text, carried on
    ///     <see cref="CompactionFailedEvent.Error"/>. It is the
    ///     only thing distinguishing a transient hiccup from a session that has lost
    ///     its history, so it is inlined rather than summarised.
    /// </param>
    /// <returns>
    ///     The sentence. Renderers wrap it in their own markup (brackets, colour,
    ///     toast title) but not the text: "compaction failed" alone does not tell a
    ///     reader that the run kept going on a shortened history, which is the part
    ///     that decides whether they intervene.
    /// </returns>
    public static string Failed(string error) =>
        $"compaction failed: {error} — continuing on truncated history";
}
