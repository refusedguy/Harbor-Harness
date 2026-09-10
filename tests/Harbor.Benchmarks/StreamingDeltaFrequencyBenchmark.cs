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
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 3)]
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

        store.Dispatch(new MessageStartEvent(partial));
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
            store.Dispatch(new MessageUpdateEvent(new TextDeltaEvent("m", chunk), partial));
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

        store.Dispatch(new MessageEndEvent(partial));
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
