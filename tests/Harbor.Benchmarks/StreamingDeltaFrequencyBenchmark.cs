using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Benchmarks;

/// <summary>
///     #46: frequencies per 1000 streaming deltas on the store path
///     (<c>AgentEvent → UiStore.Dispatch → DefaultUiProjector.Project</c>).
///     Answers "how many full projector passes per 1000 deltas" empirically:
///     every Project call is classified into fast-path (same screen instance),
///     history rebuild (Lines backing array changed) and tail rebuild
///     (TextBuffer reference changed, i.e. a <c>StreamingSync</c> flush).
///     Markdown parses on this path are 0 by construction
///     (<c>DefaultUiProjector.ResolveSpans</c> styles whole lines, no parser).
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> <c>DeltaCount</c> × (dispatch one <c>TextDeltaEvent</c>
///          into a fresh <c>UiStore</c>, then project the resulting state), wrapped in a
///          <c>MessageStart</c>/<c>MessageEnd</c> pair.</item>
///          <item><c>Payload:</c> one shared 24-char chunk reused for every delta (the shape a
///          token stream actually produces).</item>
///          <item><c>StateReset:</c> per invocation — a new <c>UiStore</c> and a new
///          <c>DefaultUiProjector</c> are built inside the row, so the transcript and the
///          projector's cache never carry over.</item>
///          <item><c>Drain:</c> none — dispatch and projection are synchronous.</item>
///          <item><c>RetainedState:</c> none across iterations (both instances are local); the
///          previous-screen / previous-lines / previous-buffer references are iteration-local
///          trackers, not state.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in this row.</item>
///          <item><c>AllocAttribution:</c> the projector output (this row measures
///          <em>frequency</em>, not time — it returns the classified counters so BDN keeps the
///          work alive).</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class StreamingDeltaFrequencyBenchmark
{
    [Params(1000, 2000)]
    public int DeltaCount;

    [Benchmark(Description = "Dispatch+project per delta; returns frequency counts")]
    public (int Dispatches, int Projects, int FastPath, int TailRebuilds, int HistoryRebuilds, int Lines) StreamDeltas()
    {
        var store = new UiStore();
        var projector = new DefaultUiProjector();
        var partial = AssistantMessage.Empty("sess", "m");

        store.Dispatch(new UiMsg.Agent(new MessageStartEvent(partial)));
        int dispatches = 1;

        int projects = 0;
        int fast = 0;
        int tail = 0;
        int history = 0;
        UiScreenModel? prevScreen = null;
        var prevLines = store.State.Lines;
        string? prevBuffer = store.State.Active.TextBuffer;
        string chunk = new('x', 24);

        for (int i = 0; i < DeltaCount; i++)
        {
            store.Dispatch(new UiMsg.Agent(new MessageUpdateEvent(new TextDeltaEvent("m", chunk), partial)));
            dispatches++;

            var state = store.State;
            var screen = projector.Project(state);
            projects++;

            if (ReferenceEquals(screen, prevScreen))
            {
                fast++;
            }

            // ImmutableArray.Equals is backing-array reference equality.
            if (!state.Lines.Equals(prevLines))
            {
                history++;
                prevLines = state.Lines;
            }

            if (!ReferenceEquals(state.Active.TextBuffer, prevBuffer))
            {
                tail++;
                prevBuffer = state.Active.TextBuffer;
            }

            prevScreen = screen;
        }

        store.Dispatch(new UiMsg.Agent(new MessageEndEvent(partial)));
        dispatches++;
        var endScreen = projector.Project(store.State);
        projects++;
        if (ReferenceEquals(endScreen, prevScreen))
        {
            fast++;
        }

        if (!store.State.Lines.Equals(prevLines))
        {
            history++;
        }

        // Return counts (and the final screen) so nothing is DCE'd away.
        GC.KeepAlive(endScreen);
        return (dispatches, projects, fast, tail, history, store.State.Lines.Length);
    }
}
