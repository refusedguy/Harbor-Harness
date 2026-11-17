// #436: engine-owned write-stage counter. The write stage genuinely lives in
// this assembly — AnsiWriter is the only type that reaches a terminal backend —
// so its instrument moves with it rather than being duplicated. The other three
// UiStageCounters stages (layout/materialize/parse) stay renderer-side in
// Harbor.Ui.Framework.Rendering.PerformanceContracts: nothing here constructs,
// reads, or aggregates them, and a second tally of the same stage is the
// failure mode (#970 shape). Production traffic for TerminalWrites flows
// through this counter from #436 on; the renderer-side counter's TerminalWrites
// no longer observes it (its write call sites moved here with the writer).
//
// #2220 adds the damage-decision counters to the same snapshot: the frame
// pipeline's hinted-scan hit and its full-scan fallback miss are the two
// outcomes of the one CellDamageContract decision, so they belong next to the
// write tally that eventually ships the frame.
namespace Harbor.Tui.CellForge.Rendering;

using System.Runtime.CompilerServices;

/// <summary>
///     Write-stage debug counters for the frame pipeline: frames actually
///     handed to the terminal backend, plus the damage-decision outcomes that
///     chose the hinted or the full scan. A frame that carried no bytes is
///     dropped by <see cref="AnsiWriter" /> before the backend is touched
///     and is deliberately <b>not</b> counted in <see cref="TerminalWrites" />:
///     "writes per frame" is a device-bound, not a method-call count.
/// </summary>
/// <remarks>
///     Opt-in like its renderer-side sibling: one static bool read and a
///     predictable branch; with <see cref="Enabled" /> false there is no
///     allocation and no atomic. Counting stays off in production and is
///     switched on by the test or the benchmark that wants the numbers.
///     <para>
///         A frame with no registered hints makes no decision and increments
///         neither <see cref="HintHits" /> nor <see cref="HintMisses" />.
///         A miss means hints were present but exceeded the canonical area
///         threshold, so the diff took the full-scan fallback. The ratio
///         <c>HintMisses / (HintHits + HintMisses)</c> is the fallback rate a
///         diagnostics run reports; it is meaningful only over a window with
///         at least one decision.
///     </para>
/// </remarks>
public static class EngineStageCounters
{
    private static volatile bool _enabled;

    private static long _terminalWrites;
    private static long _hintHits;
    private static long _hintMisses;

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

    /// <summary>Frames that used the hinted scan because damage stayed under the threshold (#2220).</summary>
    public static long HintHits => Interlocked.Read(ref _hintHits);

    /// <summary>Frames with hints that exceeded the threshold and fell back to a full scan (#2220).</summary>
    public static long HintMisses => Interlocked.Read(ref _hintMisses);

    /// <summary>The counters as one value, for a scripted run to print.</summary>
    public static EngineStageSnapshot Snapshot() => new(TerminalWrites, HintHits, HintMisses);

    /// <summary>
    ///     Zeroes all three counters. Tests call this before their window so a
    ///     preceding test cannot move the number.
    /// </summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref _terminalWrites, 0);
        Interlocked.Exchange(ref _hintHits, 0);
        Interlocked.Exchange(ref _hintMisses, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountTerminalWrite()
    {
        if (_enabled)
        {
            Interlocked.Increment(ref _terminalWrites);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountHintHit()
    {
        if (_enabled)
        {
            Interlocked.Increment(ref _hintHits);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountHintMiss()
    {
        if (_enabled)
        {
            Interlocked.Increment(ref _hintMisses);
        }
    }
}

/// <summary>One reading of <see cref="EngineStageCounters" />.</summary>
/// <param name="TerminalWrites">See <see cref="EngineStageCounters.TerminalWrites" />.</param>
/// <param name="HintHits">See <see cref="EngineStageCounters.HintHits" />.</param>
/// <param name="HintMisses">See <see cref="EngineStageCounters.HintMisses" />.</param>
public readonly record struct EngineStageSnapshot(
    long TerminalWrites,
    long HintHits,
    long HintMisses);
