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
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one pass of <c>MessageStart + DeltaCount × TextDelta +
///          MessageEnd</c> through the pure <c>AppReducer</c> (or <c>DeltaCount</c> state
///          clones for the floor row).</item>
///          <item><c>Payload:</c> <c>DeltaCount</c> deltas of ~19 chars each ("Token 00000 —
///          "), pre-built once in <c>Setup</c>; no session ids, no tool args.</item>
///          <item><c>StateReset:</c> per invocation — both rows start from a freshly
///          constructed <c>AppState</c>, so the streaming buffer never carries over between
///          iterations.</item>
///          <item><c>Drain:</c> none — the reducer is synchronous and the whole stream is
///          folded in one call.</item>
///          <item><c>RetainedState:</c> none between iterations; the <c>ChunkedBuffer</c> is
///          owned by the state instance the row creates.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in either row.</item>
///          <item><c>AllocAttribution:</c> the per-event immutable snapshot
///          (<c>with</c>-clone) plus the delta buffer. The floor row isolates the snapshot
///          cost: <c>full-stream − floor</c> is what the streaming machinery costs.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
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
