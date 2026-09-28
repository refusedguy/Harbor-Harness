using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Reducers;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref=\"AppStore.Dispatch\" /> — the Redux-style
///     dispatch loop that applies <see cref=\"AgentEvent\" />s through
///     <see cref=\"AppReducer\" /> to produce an immutable <see cref=\"AppState\" />.
///     Measures the per-event overhead of pattern-matching + record cloning
///     (<c>with</c> expressions) on state trees of varying size.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> <c>LineCount</c> dispatches into a fresh <see
///          cref="AppStore" /> (TextDelta, ToolExecutionStart or
///          StepFinish-with-usage).</item>
///          <item><c>Payload:</c> <c>LineCount</c> short events pre-built in <c>Setup</c>; the
///          tool-call row parses its <c>{}</c> args once per op (inside the measured
///          window).</item>
///          <item><c>StateReset:</c> per invocation — every row allocates its own
///          <c>AppStore</c>, so the transcript never grows across iterations.</item>
///          <item><c>Drain:</c> none — dispatch is synchronous; there is no background
///          projector loop in this class.</item>
///          <item><c>RetainedState:</c> none. The store instance dies with the op; only the
///          returned final <c>AppState</c> escapes.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> one snapshot clone per event plus the transcript
///          append. Rows differ in payload only, so their per-event deltas are directly
///          comparable.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class AppStoreDispatchBenchmark
{
    private AppStore _store = null!;
    private AgentEvent[] _events = null!;

    [Params(10, 100, 1000)]
    public int LineCount;

    [GlobalSetup]
    public void Setup()
    {
        _store = new AppStore();

        _events = new AgentEvent[LineCount];
        for (int i = 0; i < LineCount; i++)
        {
            _events[i] = new MessageUpdateEvent(
                new TextDeltaEvent($"msg-{i}", $"Token {i} "),
                AssistantMessage.Empty("session-1", "stub-1"));
        }
    }

    [Benchmark(Description = "Dispatch N TextDeltaEvent", Baseline = true)]
    public AppState Dispatch_N_TextDeltas()
    {
        var store = new AppStore();
        for (int i = 0; i < _events.Length; i++)
            store.Dispatch(_events[i]);
        return store.State;
    }

    [Benchmark(Description = "Dispatch N ToolCallStartEvent")]
    public AppState Dispatch_N_ToolCallStarts()
    {
        var store = new AppStore();
        var events = new AgentEvent[LineCount];
        for (int i = 0; i < LineCount; i++)
        {
            events[i] = new ToolExecutionStartEvent(
                $"tc_{i}",
                $"tool_{i}",
                System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone());
        }
        for (int i = 0; i < events.Length; i++)
            store.Dispatch(events[i]);
        return store.State;
    }

    [Benchmark(Description = "Dispatch N StepFinishEvent with usage")]
    public AppState Dispatch_N_StepFinishes()
    {
        var store = new AppStore();
        var events = new AgentEvent[LineCount];
        for (int i = 0; i < LineCount; i++)
        {
            events[i] = new MessageUpdateEvent(
                new StepFinishEvent(i % 100, "stop", new Usage(100 + i, 50 + i)),
                AssistantMessage.Empty("session-1", "stub-1"));
        }
        for (int i = 0; i < events.Length; i++)
            store.Dispatch(events[i]);
        return store.State;
    }
}
