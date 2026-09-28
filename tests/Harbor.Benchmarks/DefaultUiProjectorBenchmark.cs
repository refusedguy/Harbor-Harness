using BenchmarkDotNet.Attributes;
using System.Collections.Immutable;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref=\"DefaultUiProjector.Project\" /> — the
///     projection of <see cref=\"UiState\" /> into <see cref=\"UiScreenModel\" />.
///     Measures the cost of building <see cref=\"UiRenderedLine\" /> arrays,
///     resolving <see cref=\"StyledSpan\" /> lists, and computing the state
///     revision string.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one <c>DefaultUiProjector.Project</c> call over a
///          <c>LineCount</c>-line transcript (plus <c>ExtractRenderedLines</c> on the third
///          row).</item>
///          <item><c>Payload:</c> <c>LineCount</c> chat lines of 40–120 chars, four roles
///          interleaved, built once in <c>Setup</c>.</item>
///          <item><c>StateReset:</c> per invocation — the cold and extract rows force a cache
///          miss with a distinct record instance (<c>_state with { ... }</c>); the cached-hit
///          row deliberately does not.</item>
///          <item><c>Drain:</c> none — projection is synchronous and returns a screen model.</item>
///          <item><c>RetainedState:</c> the projector's internal memoisation cache, which is
///          why there are two rows: <c>Project_UiState_CachedHit</c> measures the
///          <b>retained</b> cache (same instance → ~8 ns) and is intentionally never reset
///          between iterations — resetting it would delete the thing being measured.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> rendered-line array + styled-span lists + the
///          revision string. Cache-hit row allocates nothing; the cached-vs-cold delta is the
///          projection itself.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class DefaultUiProjectorBenchmark
{
    private UiState _state = null!;
    private DefaultUiProjector _projector = null!;

    [Params(1, 50, 500, 5000)]
    public int LineCount;

    [GlobalSetup]
    public void Setup()
    {
        _projector = new DefaultUiProjector();
        var lines = new ChatLine[LineCount];
        for (int i = 0; i < LineCount; i++)
        {
            lines[i] = new ChatLine(
                i % 4 == 0 ? ChatRole.User : i % 4 == 1 ? ChatRole.Assistant : i % 4 == 2 ? ChatRole.Tool : ChatRole.ToolResult,
                $"Message {i}: " + new string('x', 40 + (i % 80)),
                i % 4 == 2 ? $"tc_{i}" : null,
                $"msg-{i}",
                default);
        }

        _state = new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                Input = new InputModel("test prompt", ImmutableArray<string>.Empty, -1),
                Focus = FocusMode.Input,
                ScrollOffset = 0,
                ViewportLines = 40,
                TotalLines = LineCount
            },
            Chat = ChatDomainState.Empty with
            {
                Lines = lines.ToImmutableArray(),
                IsStreaming = true,
                Active = new ActiveMessage("Streaming assistant response text", "Streaming thinking text"),
                Status = "running",
                Cost = new CostSnapshot(10000, 5000, 0.50m),
                Model = "gpt-4",
                Provider = "openai",
                AgentName = "code",
                IsAgentRunning = true,
                WasRunning = false
            }
        };
    }

    [Benchmark(Description = "Project UiState -> UiScreenModel (cold, distinct instance)", Baseline = true)]
    public UiScreenModel Project_UiState()
    {
        // Force cache miss: new record instance defeats ReferenceEquals hit that gave 8ns.
        var fresh = _state with { Ui = _state.Ui with { ScrollOffset = _state.Ui.ScrollOffset } };
        return _projector.Project(fresh);
    }

    [Benchmark(Description = "Project UiState -> UiScreenModel (cached hit, same ref)")]
    public UiScreenModel Project_UiState_CachedHit()
    {
        return _projector.Project(_state);
    }

    [Benchmark(Description = "ExtractRenderedLines from projected screen")]
    public ImmutableArray<UiRenderedLine> ExtractRenderedLines()
    {
        var fresh = _state with { Ui = _state.Ui with { ScrollOffset = _state.Ui.ScrollOffset } };
        var screen = _projector.Project(fresh);
        return DefaultUiProjector.ExtractRenderedLines(screen);
    }
}
