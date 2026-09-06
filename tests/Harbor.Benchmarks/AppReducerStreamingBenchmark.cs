using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Reducers;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Benchmarks;

/// <summary>
///     Streaming-delta cost of <see cref="AppReducer" /> — the P0 bottleneck
///     from docs/BENCHMARKS.md (19.4 MB per 1000 TextDelta with O(N²) string
///     concatenation, before the ChunkedBuffer + StreamingSync rework).
///     A realistic assistant message stream: MessageStart, 1000 text deltas
///     (~19 chars each — the baseline scenario), MessageEnd, all dispatched
///     through the pure reducer. Allocation budget target: &lt; 100 KB.
/// </summary>
/// <remarks>
///     <see cref="SnapshotFloor_1000Clones" /> measures the irreducible
///     per-event cost of the immutable-snapshot (MVU) architecture itself —
///     1000 <c>with</c>-clones of a realistic <see cref="AppState" /> with no
///     streaming-buffer work at all. The delta between the full-stream
///     benchmark and this floor is what the streaming concat machinery
///     (ChunkedBuffer + materialization) actually costs.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class AppReducerStreamingBenchmark
{
    private AgentEvent[] _events = null!;

    [Params(1000)]
    public int DeltaCount;

    [GlobalSetup]
    public void Setup()
    {
        var partial = AssistantMessage.Empty("session-1", "stub-1");
        _events = new AgentEvent[DeltaCount + 2];
        _events[0] = new MessageStartEvent(partial);
        for (int i = 0; i < DeltaCount; i++)
        {
            // ~19 chars per delta — matches the O(N²) baseline scenario.
            _events[i + 1] = new MessageUpdateEvent(
                new TextDeltaEvent($"msg-1", $"Token {i:00000} — "),
                partial);
        }

        _events[^1] = new MessageEndEvent(partial);
    }

    [Benchmark(Description = "MessageStart + N TextDelta + MessageEnd through AppReducer", Baseline = true)]
    public AppState Stream_OneMessage()
    {
        AppState state = new AppState();
        for (int i = 0; i < _events.Length; i++)
        {
            state = AppReducer.Reduce(_events[i], state);
        }

        return state;
    }

    [Benchmark(Description = "Snapshot floor: N AppState with-clones, no buffer work")]
    public AppState SnapshotFloor_1000Clones()
    {
        // A realistic mid-session state: populated transcript and streaming
        // buffers, so the clone cost matches what the full-stream benchmark
        // pays per event. Only the pending buffer changes per clone.
        AppState state = new AppState
        {
            Status = "running",
            IsAgentRunning = true,
            IsStreaming = true,
            Model = "claude-opus-4",
            Provider = "anthropic",
            AgentName = "code",
            StreamingBuffer = new string('x', DeltaCount * 19),
            Lines = [new ChatLine(ChatRole.User, "Write a C# function that reverses a string.")],
        };

        for (int i = 0; i < DeltaCount; i++)
        {
            state = state with { ScrollOffset = i };
        }

        return state;
    }
}
