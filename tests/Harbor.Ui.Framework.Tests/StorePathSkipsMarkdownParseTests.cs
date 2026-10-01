using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Rendering.Markdown;
using Harbor.Ui.Framework.Rendering.PerformanceContracts;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #46 slice 2 (#409) — the store path's half of the stage-counter story.
/// </summary>
/// <remarks>
///     <para>
///         <c>StreamingDeltaFrequencyBenchmark</c> already recorded
///         "markdown-парсов на store-пути 0" as prose in its class doc, and
///         <c>docs/BENCHMARKS.md</c> carries it as a table cell. Neither can
///         fail: a doc comment is not an assertion, so if a future change made
///         the projector start parsing, the claim would go stale silently and
///         nothing would notice. This suite makes it checkable — and, more
///         importantly, makes it <b>falsifiable in both directions</b>.
///     </para>
///     <para>
///         The danger specific to a "== 0" assertion is that it passes for the
///         wrong reason: a counter that never fires anywhere also reports 0.
/// #591 is exactly that shape — an instrument that under-reports and hands
///         back a plausible zero. So the positive control lives in the same
///         file: <see cref="SameCounter_ReachesNonZero_WhenAParseReallyHappens" />
///         drives the renderer directly and requires 1, which is what makes the
///         0 below a measurement rather than a tautology about a wiring that
///         does not exist.
///     </para>
///     <para>
///         The bare <see cref="NotInParallelAttribute" /> is load-bearing for the same reason it is
///         in <c>UiStageCounterTests</c>: the counters are process-global by contract, a keyed
///         attribute only excludes tests that share that key, and any concurrently-running test
///         touching the instrumented renderer would move the number this suite asserts is zero.
///         Exclusive execution is what makes "zero here" a measurement rather than a race that
///         happened to land.
///     </para>
/// </remarks>
[NotInParallel]
public class StorePathSkipsMarkdownParseTests
{
    private const int DeltaCount = 200;

    /// <summary>
    ///     Drives N deltas through the store and projects each one, exactly the
    ///     shape <c>StreamingDeltaFrequencyBenchmark</c> measures, and requires
    ///     the parse counter to stay at zero throughout.
    /// </summary>
    [Test]
    public async Task StoreDispatchAndProjection_NeverParseMarkdown()
    {
        UiStageCounters.Reset();
        UiStageCounters.Enabled = true;
        long materializations;
        long parses;
        try
        {
            var store = new UiStore();
            var projector = new DefaultUiProjector();
            var partial = AssistantMessage.Empty("s", "m");
            store.Dispatch(new ChatAppMsg.Agent(new MessageStartEvent(partial)));

            var chunk = new string('x', 24);
            for (int i = 0; i < DeltaCount; i++)
            {
                store.Dispatch(new ChatAppMsg.Agent(new MessageUpdateEvent(new TextDeltaEvent("m", chunk), partial)));
                _ = projector.Project(store.State);
            }

            store.Dispatch(new ChatAppMsg.Agent(new MessageEndEvent(partial)));
            _ = projector.Project(store.State);

            parses = UiStageCounters.MarkdownParses;
            materializations = UiStageCounters.Materializations;
        }
        finally
        {
            UiStageCounters.Enabled = false;
            UiStageCounters.Reset();
        }

        await Assert.That(parses)
            .IsEqualTo(0)
            .Because(
                $"DefaultUiProjector.ResolveSpans styles whole lines and never constructs a parser, "
                + $"so {DeltaCount} deltas plus a projection each must reach ParseInto zero times. "
                + $"A non-zero here means the projector started parsing and the BENCHMARKS.md cell is now false");
        await Assert.That(materializations)
            .IsEqualTo(0)
            .Because(
                "the markdown renderer is not on the store path either — it lives behind the CellForge "
                + "layout/measure stage, which this path never enters");
    }

    /// <summary>
    ///     The positive control, in the same file so the zero above cannot be
    ///     read as a statement about a counter that is simply never wired. One
    ///     fresh push and render must move the parse counter to exactly 1.
    /// </summary>
    [Test]
    public async Task SameCounter_ReachesNonZero_WhenAParseReallyHappens()
    {
        UiStageCounters.Reset();
        UiStageCounters.Enabled = true;
        long parses;
        try
        {
            var renderer = new StreamingMarkdownRenderer();
            renderer.Push("# heading\n\n- one\n- two\n");
            _ = renderer.RenderTail(80);
            parses = UiStageCounters.MarkdownParses;
        }
        finally
        {
            UiStageCounters.Enabled = false;
            UiStageCounters.Reset();
        }

        await Assert.That(parses)
            .IsEqualTo(1)
            .Because(
                "the counter reaches 1 the moment a parse genuinely happens, so the zero asserted by "
                + "StoreDispatchAndProjection_NeverParseMarkdown is a property of that path, not of the wiring");
    }
}