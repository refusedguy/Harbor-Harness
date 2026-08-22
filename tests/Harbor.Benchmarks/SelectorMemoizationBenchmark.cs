using BenchmarkDotNet.Attributes;
using System.Collections.Immutable;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks selector-style computations over <see cref=\"AppState\" /> —
///     the derived-data extractions that run on every dispatch to decide
///     what the UI should render. Measures the cost of scanning immutable
///     arrays, computing aggregates, and cloning sub-snapshots.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class SelectorMemoizationBenchmark
{
    private AppState _state = null!;

    [Params(0, 100, 1000, 10000)]
    public int LineCount;

    [GlobalSetup]
    public void Setup()
    {
        var lines = new ChatLine[LineCount];
        for (int i = 0; i < LineCount; i++)
        {
            lines[i] = new ChatLine(
                i % 3 == 0 ? ChatRole.User : i % 3 == 1 ? ChatRole.Assistant : ChatRole.Tool,
                $"Line {i}: " + new string('x', 20 + (i % 50)),
                i % 3 == 2 ? $"tc_{i}" : null,
                null,
                default);
        }

        _state = new AppState
        {
            Lines = lines.ToImmutableArray(),
            IsStreaming = true,
            Active = new ActiveMessage("Active streaming text buffer with content", "Active thinking buffer"),
            IsThinking = true,
            Status = "running",
            Cost = new CostSnapshot(5000, 2000, 0.12m),
            Model = "test-model",
            Provider = "test-provider",
            AgentName = "code",
            IsAgentRunning = true,
            WasRunning = false,
            Input = new InputModel("test input", ImmutableArray<string>.Empty, -1),
            Focus = FocusMode.Input,
            ScrollOffset = 0,
            ViewportLines = 40,
            TotalLines = LineCount,
            PanelStates = ImmutableDictionary<string, TuiPanelState>.Empty,
            PanelSizes = ImmutableDictionary<string, int>.Empty,
            ActiveDrawerTab = "None",
            StreamingBuffer = "streaming",
            ThinkingBuffer = "thinking",
            Chrome = new AppState.ChromeState
            {
                ActiveSessionId = SessionId.Create("session-1"),
                NavigationStack = ImmutableStack<AppState.Route>.Empty,
                ActiveModal = null,
                Toasts = ImmutableArray<AppState.Toast>.Empty
            }
        };
    }

    [Benchmark(Description = "Select Lines.Length", Baseline = true)]
    public int Select_LinesLength()
    {
        int sum = 0;
        for (int i = 0; i < 1000; i++)
            sum += _state.Lines.Length;
        return sum;
    }

    [Benchmark(Description = "Select ScrollPercent")]
    public int Select_ScrollPercent()
    {
        int sum = 0;
        for (int i = 0; i < 1000; i++)
            sum += _state.ScrollPercent;
        return sum;
    }

    [Benchmark(Description = "Select cost snapshot")]
    public CostSnapshot Select_CostSnapshot()
    {
        CostSnapshot sum = default;
        for (int i = 0; i < 1000; i++)
            sum = _state.Cost;
        return sum;
    }

    [Benchmark(Description = "Filter assistant lines")]
    public int Filter_AssistantLines()
    {
        int sum = 0;
        for (int i = 0; i < 100; i++)
        {
            int count = 0;
            foreach (var line in _state.Lines)
            {
                if (line.Role == ChatRole.Assistant)
                    count++;
            }
            sum += count;
        }
        return sum;
    }

    [Benchmark(Description = "Compute total text length")]
    public long Compute_TotalTextLength()
    {
        long sum = 0;
        for (int i = 0; i < 100; i++)
        {
            long total = 0;
            foreach (var line in _state.Lines)
                total += line.Text.Length;
            sum += total;
        }
        return sum;
    }
}
