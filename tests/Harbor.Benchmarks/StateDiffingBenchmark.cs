using BenchmarkDotNet.Attributes;
using System.Collections.Immutable;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks structural equality and diffing of <see cref="UiState" />
///     snapshots — the operation renderers perform to decide whether a
///     full repaint is necessary. Measures <see cref="EqualityComparer" />
///     on immutable record trees of varying depth, plus manual field-by-field
///     comparison for early-exit scenarios.
/// </summary>
/// <para>
///     <b>Repointed by #597.</b> This benchmark used to measure <c>AppState</c>,
///     the flat pre-split record that #594's deletion left producer-less. The
///     measurement is unchanged in kind and the payload is unchanged in shape:
///     <c>AppState</c>'s transcript/status/streaming fields are
///     <c>UiState.Chat</c>'s, and its input/focus/scroll fields are
///     <c>UiState.Ui</c>'s. What is measured is now the state every renderer
///     actually reads, so the number prices the real fold rather than a shape
///     nothing constructs.
/// </para>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one structural equality check between two <see
///          cref="UiState" /> snapshots (identical reference, changed value), one hand-rolled
///          early-exit field comparison, or one line-count delta.</item>
///          <item><c>Payload:</c> a <c>LineCount</c>-line transcript; the "changed" snapshot
///          differs in <c>Chat.Status</c>, <c>Chat.IsStreaming</c>, <c>Chat.Active.TextBuffer</c>
///          and one usage counter.</item>
///          <item><c>StateReset:</c> none — the three snapshots are immutable and built once
///          in <c>Setup</c>; no equality call mutates them.</item>
///          <item><c>Drain:</c> none — comparison is synchronous.</item>
///          <item><c>RetainedState:</c> none.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> zero for every row — a value-shape change is
///          exactly what must not allocate. The <c>ManualCompare_EarlyExit</c> row prices the
///          alternative (three scalar compares) that renderers use to skip a full
///          repaint.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class StateDiffingBenchmark
{
    private UiState _oldState = null!;
    private UiState _newState = null!;
    private UiState _identicalState = null!;

    [Params(0, 100, 1000)]
    public int LineCount;

    [GlobalSetup]
    public void Setup()
    {
        var lines = new ChatLine[LineCount];
        for (int i = 0; i < LineCount; i++)
        {
            lines[i] = new ChatLine(
                ChatRole.Assistant,
                $"Line {i}",
                null,
                $"msg-{i}",
                default);
        }

        _oldState = new UiState
        {
            Ui = new TerminalUiState
            {
                Input = new InputModel("hello", ImmutableArray<string>.Empty, -1),
                Focus = FocusMode.Input,
                ScrollOffset = 0,
                ViewportLines = 40,
                TotalLines = LineCount
            },
            Chat = new ChatDomainState
            {
                Lines = lines.ToImmutableArray(),
                Status = "idle",
                IsStreaming = false,
                Active = ActiveMessage.Empty,
                Cost = new CostSnapshot(1000, 500, 0.05m),
                Model = "model-a",
                Provider = "provider-a",
                AgentName = "code",
                IsAgentRunning = false,
                WasRunning = false
            }
        };

        _newState = _oldState with
        {
            Chat = _oldState.Chat with
            {
                Status = "running",
                IsStreaming = true,
                Active = new ActiveMessage("partial text", string.Empty),
                Cost = _oldState.Chat.Cost with { TokensOut = 501 }
            }
        };

        _identicalState = _oldState;
    }

    [Benchmark(Description = "Record.Equals (identical state)", Baseline = true)]
    public bool Equals_Identical() => _oldState.Equals(_identicalState);

    [Benchmark(Description = "Record.Equals (changed state)")]
    public bool Equals_Changed() => _oldState.Equals(_newState);

    [Benchmark(Description = "Manual field comparison (early exit on Status)")]
    public bool ManualCompare_EarlyExit()
    {
        if (ReferenceEquals(_oldState, _newState)) return true;
        if (_oldState.Chat.Status != _newState.Chat.Status) return false;
        if (_oldState.Chat.IsStreaming != _newState.Chat.IsStreaming) return false;
        if (_oldState.Chat.Active.TextBuffer != _newState.Chat.Active.TextBuffer) return false;
        return true;
    }

    [Benchmark(Description = "Compute Lines.Length delta")]
    public int Compute_LinesDelta() => Math.Abs(_oldState.Chat.Lines.Length - _newState.Chat.Lines.Length);
}