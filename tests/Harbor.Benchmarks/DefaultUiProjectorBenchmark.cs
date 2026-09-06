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
            Lines = lines.ToImmutableArray(),
            IsStreaming = true,
            Active = new ActiveMessage("Streaming assistant response text", "Streaming thinking text"),
            Status = "running",
            Cost = new CostSnapshot(10000, 5000, 0.50m),
            Model = "gpt-4",
            Provider = "openai",
            AgentName = "code",
            IsAgentRunning = true,
            WasRunning = false,
            Input = new InputModel("test prompt", ImmutableArray<string>.Empty, -1),
            Focus = FocusMode.Input,
            ScrollOffset = 0,
            ViewportLines = 40,
            TotalLines = LineCount
        };
    }

    [Benchmark(Description = "Project UiState -> UiScreenModel (cold, distinct instance)", Baseline = true)]
    public UiScreenModel Project_UiState()
    {
        // Force cache miss: new record instance defeats ReferenceEquals hit that gave 8ns.
        var fresh = _state with { ScrollOffset = _state.ScrollOffset };
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
        var fresh = _state with { ScrollOffset = _state.ScrollOffset };
        var screen = _projector.Project(fresh);
        return DefaultUiProjector.ExtractRenderedLines(screen);
    }
}
