// #436: engine-owned write-stage counter. The write stage genuinely lives in
// this assembly — AnsiWriter is the only type that reaches a terminal backend —
// so its instrument moves with it rather than being duplicated. The other three
// UiStageCounters stages (layout/materialize/parse) stay renderer-side in
// Harbor.Ui.Framework.Rendering.PerformanceContracts: nothing here constructs,
// reads, or aggregates them, and a second tally of the same stage is the
// failure mode (#970 shape). Production traffic for TerminalWrites flows
// through this counter from #436 on; the renderer-side counter's TerminalWrites
// no longer observes it (its write call sites moved here with the writer).
namespace Harbor.Tui.CellForge.Rendering;

using System.Runtime.CompilerServices;

/// <summary>
///     Write-stage debug counter for the frame pipeline: frames actually
///     handed to the terminal backend. A frame that carried no bytes is
///     dropped by <see cref="AnsiWriter" /> before the backend is touched
///     and is deliberately <b>not</b> counted: "writes per frame" is a
///     device-bound, not a method-call count.
/// </summary>
/// <remarks>
///     Opt-in like its renderer-side sibling: one static bool read and a
///     predictable branch; with <see cref="Enabled" /> false there is no
///     allocation and no atomic. Counting stays off in production and is
///     switched on by the test or the benchmark that wants the numbers.
/// </remarks>
public static class EngineStageCounters
{
    private static volatile bool _enabled;

    private static long _terminalWrites;

    /// <summary>
    ///     Master switch. Off by default. Set it before the work you want
    ///     counted, and back to <see langword="false" /> after — a test that
    ///     leaves it on pays for every later test in the process.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Frames actually handed to the terminal backend since the last <see cref="Reset" />.</summary>
    public static long TerminalWrites => Interlocked.Read(ref _terminalWrites);

    /// <summary>The counter as one value, for a scripted run to print.</summary>
    public static EngineStageSnapshot Snapshot() => new(TerminalWrites);

    /// <summary>
    ///     Zeroes the counter. Tests call this before their window so a
    ///     preceding test cannot move the number.
    /// </summary>
    public static void Reset() => Interlocked.Exchange(ref _terminalWrites, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountTerminalWrite()
    {
        if (_enabled)
        {
            Interlocked.Increment(ref _terminalWrites);
        }
    }
}

/// <summary>One reading of <see cref="EngineStageCounters" />.</summary>
/// <param name="TerminalWrites">See <see cref="EngineStageCounters.TerminalWrites" />.</param>
public readonly record struct EngineStageSnapshot(long TerminalWrites);
