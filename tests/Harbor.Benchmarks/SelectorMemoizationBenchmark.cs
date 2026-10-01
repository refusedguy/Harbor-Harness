using BenchmarkDotNet.Attributes;
using System.Collections.Immutable;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks selector-style computations over <see cref="UiState" /> —
///     the derived-data extractions that run on every dispatch to decide
///     what the UI should render. Measures the cost of scanning immutable
///     arrays, computing aggregates, and cloning sub-snapshots.
/// </summary>
/// <para>
///     <b>Repointed by #597.</b> This benchmark used to measure <c>AppState</c>,
///     the flat pre-split record that #594's deletion left producer-less. The rows
///     are the same selectors over the same shapes: the transcript and status
///     selectors read <c>UiState.Chat</c>, the scroll and panel selectors read
///     <c>UiState.Ui</c>. The one row with no counterpart is
///     <c>Select_CostSnapshot</c>, which is kept and reads <c>Chat.Cost</c>.
/// </para>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> a derived-data extraction over <see cref="UiState" />,
///          repeated 100× (or 1000× for the O(1) selectors) inside one op.</item>
///          <item><c>Payload:</c> a <c>LineCount</c>-line transcript with three interleaved
///          roles, built once in <c>Setup</c>.</item>
///          <item><c>StateReset:</c> none — the state is immutable and read-only, so every op
///          sees exactly the same input.</item>
///          <item><c>Drain:</c> none — the selectors are synchronous.</item>
///          <item><c>RetainedState:</c> none; the immutable arrays are shared but never
///          mutated.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> the scan rows (<c>Filter_AssistantLines</c>,
///          <c>Compute_TotalTextLength</c>) allocate nothing; the field-copy rows copy a
///          record by value. The inner-loop multiplier means the reported mean is per
///          extraction, not per op.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class SelectorMemoizationBenchmark
{
    private UiState _state = null!;

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

        _state = new UiState
        {
            Ui = new TerminalUiState
            {
                Input = new InputModel("test input", ImmutableArray<string>.Empty, -1),
                Focus = FocusMode.Input,
                ScrollOffset = 0,
                ViewportLines = 40,
                TotalLines = LineCount,
                PanelStates = ImmutableDictionary<string, TuiPanelState>.Empty,
                PanelSizes = ImmutableDictionary<string, int>.Empty
            },
            Chat = new ChatDomainState
            {
                Lines = lines.ToImmutableArray(),
                IsStreaming = true,
                Active = new ActiveMessage("Active streaming text buffer with content", "Active thinking buffer"),
                Status = "running",
                Cost = new CostSnapshot(5000, 2000, 0.12m),
                Model = "test-model",
                Provider = "test-provider",
                AgentName = "code",
                IsAgentRunning = true,
                WasRunning = false
            }
        };
    }

    [Benchmark(Description = "Select Lines.Length", Baseline = true)]
    public int Select_LinesLength()
    {
        int sum = 0;
        for (int i = 0; i < 1000; i++)
            sum += _state.Chat.Lines.Length;
        return sum;
    }

    [Benchmark(Description = "Select ScrollPercent")]
    public int Select_ScrollPercent()
    {
        int sum = 0;
        for (int i = 0; i < 1000; i++)
            sum += _state.Ui.ScrollPercent;
        return sum;
    }

    [Benchmark(Description = "Select cost snapshot")]
    public CostSnapshot Select_CostSnapshot()
    {
        CostSnapshot sum = default;
        for (int i = 0; i < 1000; i++)
            sum = _state.Chat.Cost;
        return sum;
    }

    [Benchmark(Description = "Filter assistant lines")]
    public int Filter_AssistantLines()
    {
        int sum = 0;
        for (int i = 0; i < 100; i++)
        {
            int count = 0;
            foreach (var line in _state.Chat.Lines)
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
            foreach (var line in _state.Chat.Lines)
                total += line.Text.Length;
            sum += total;
        }
        return sum;
    }
}