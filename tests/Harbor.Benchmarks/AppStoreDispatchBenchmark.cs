using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Reducers;
using Harbor.Ui.Framework.State;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref=\"AppStore.Dispatch\" /> — the Redux-style
///     dispatch loop that applies <see cref=\"AgentEvent\" />s through
///     <see cref=\"AppReducer\" /> to produce an immutable <see cref=\"AppState\" />.
///     Measures the per-event overhead of pattern-matching + record cloning
///     (<c>with</c> expressions) on state trees of varying size.
/// </summary>
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
