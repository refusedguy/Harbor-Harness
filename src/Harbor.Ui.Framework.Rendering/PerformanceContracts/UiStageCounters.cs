namespace Harbor.Ui.Framework.Rendering.PerformanceContracts;

using System.Runtime.CompilerServices;

/// <summary>
///     #46 slice 2 (#409) — per-stage debug counters for the
///     <c>TextDelta</c> → visible-frame pipeline, so the next optimization round
///     is aimed at a measured stage instead of a guess.
/// </summary>
/// <remarks>
///     <para>
///         <b>These four names are NOT four sequential stages.</b> On the
///         shipped <c>CellForge</c> path the first three are a single nested
///         call chain, and reading them as a budget is a category error:
///     </para>
///     <list type="bullet">
///         <item><description>
///             <b>layout</b> — <c>TimelineLayoutCache.PrepareLayout</c> →
///             <c>SettleVisible</c> → <c>IChatBlock.Measure</c>;
///         </description></item>
///         <item><description>
///             <b>materialize</b> — <c>EnsureRendered</c> / <c>RenderTail</c>,
///             which <c>Measure</c> calls <i>first</i>, so a measure that has to
///             know its own height also renders the block;
///         </description></item>
///         <item><description>
///             <b>parse</b> — <c>MarkdownBlockParser.ParseInto</c>, reached only
///             from inside a materialize that survived its own memo;
///         </description></item>
///         <item><description>
///             <b>write</b> — <c>AnsiWriter</c> handing a frame to the backend.
///             This one <i>is</i> a genuinely separate stage: a different
///             assembly (<c>Harbor.Tui.CellForge.Engine</c>), downstream of
///             paint, and the only one that reaches a device.
///         </description></item>
///     </list>
///     <para>
///         So the invariant is <c>TerminalWrites ≥ Materializations ≥ MarkdownParses</c>
///         per frame, and <see cref="BlocksLaidOut" /> sits above all three:
///         the sum of the four is meaningless and no gate may assert on it. What
///         the counters buy is <i>frequency</i> — "how many materializations per
///         1000 deltas", "zero parses on the store path" — which is exactly
///         what #410 gates and what #412 needs to prove virtualization is real.
///     </para>
///     <para>
///         <b>Cost, measured (not estimated).</b> One
///         <see cref="Interlocked" />-increment behind this guard costs
///         <b>≈ 7.6 ns</b> on the probe machine (20 M iterations, Release; a
///         single-file probe with the same guard/counter shape, gated on
///         non-vacuity: guard off counted exactly 0 events, guard on exactly
///         one per call, and on was strictly slower than off). Every counter
///         here fires <b>per frame</b> or rarer — never per delta — so all four
///         together cost ≈ 42 ns per frame against a measured 51.3 µs idle frame
///         and an 805 µs solve+paint+diff+encode frame
///         (<c>docs/BENCHMARKS.md</c>): <b>0.08 % of an idle frame</b>. The
///         per-delta hot path is deliberately left uninstrumented for the same
///         reason #984 struck rows instead of converting them — a per-frame
///         budget is not "once per call", and a counter there would have cost
///         1.59 % of the 475 ns/delta store path this file's siblings measure.
///     </para>
///     <para>
///         <b>Why opt-in rather than always-on.</b> The guard is one static bool
///         read and a predictable branch on a field the calling assembly already
///         touched; with <see cref="Enabled" /> false there is no allocation and
///         no atomic. Counting stays off in production and is switched on by the
///         test or the benchmark that wants the numbers — the same trade
///         <see cref="UnicodeWidth.BeginWidthLookupTracking" /> makes for #487,
///         and for the same reason.
///     </para>
///     <para>
///         <b>Why <see cref="Interlocked" /> and not a thread-static.</b>
///         <see cref="UnicodeWidth" /> can use a thread-static because its
///         lookup is render-thread-only and its consumer is a single-threaded
///         test. These counters cannot: <c>StreamingMarkdownBlock.Push</c> runs on
///         the event thread while the frame loop renders on its own, so the four
///         values have to be process-global to mean anything, and
///         <c>Enabled</c> is read across threads too. Process-global longs also
///         mean a test reading them is immune to which TUnit worker ran the
///         work — the #46 hazard the issue names as "leaked/changing state".
///     </para>
/// </remarks>
public static class UiStageCounters
{
    private static volatile bool _enabled;

    private static long _blocksLaidOut;
    private static long _materializations;
    private static long _markdownParses;
    private static long _terminalWrites;

    /// <summary>
    ///     Master switch. Off by default: the render path pays one predictable
    ///     branch and nothing else. Set it before the work you want counted, and
    ///     back to <see langword="false" /> after — a test that leaves it on
    ///     pays for every later test in the process.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    ///     Blocks handed to <c>IChatBlock.Measure</c> since the last
    ///     <see cref="Reset" />. This is the number
    ///     <c>TimelineLayoutCache.MeasureCallsLastFrame</c> reports for the
    ///     current frame, accumulated — #412 asserts its bounds against this.
    /// </summary>
    public static long BlocksLaidOut => Interlocked.Read(ref _blocksLaidOut);

    /// <summary>
    ///     Times a block actually rebuilt its display lines, counted at the
    ///     memo that has already passed — so the Measure-then-Paint pair every
    ///     frame issues counts <b>once</b>, not twice, and a width-stable frame
    ///     counts zero.
    /// </summary>
    public static long Materializations => Interlocked.Read(ref _materializations);

    /// <summary>
    ///     <c>MarkdownBlockParser.ParseInto</c> calls. Zero by construction on
    ///     the store path (<c>DefaultUiProjector.ResolveSpans</c> styles whole
    ///     lines and never parses), which is the shape #410 gates.
    /// </summary>
    public static long MarkdownParses => Interlocked.Read(ref _markdownParses);

    /// <summary>
    ///     Frames actually handed to the terminal backend. A frame that carried
    ///     no bytes is dropped by <c>AnsiWriter</c> before the backend is
    ///     touched and is deliberately <b>not</b> counted: "writes per frame" is
    ///     a device-bound, not a method-call count.
    /// </summary>
    public static long TerminalWrites => Interlocked.Read(ref _terminalWrites);

    /// <summary>All four counters as one value, for a scripted run to print.</summary>
    public static UiStageSnapshot Snapshot() =>
        new(BlocksLaidOut, Materializations, MarkdownParses, TerminalWrites);

    /// <summary>
    ///     Zeroes all four. Tests call this before their window so a preceding
    ///     test cannot move the number — and so a failure names this test's
    ///     work rather than the suite's history.
    /// </summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref _blocksLaidOut, 0);
        Interlocked.Exchange(ref _materializations, 0);
        Interlocked.Exchange(ref _markdownParses, 0);
        Interlocked.Exchange(ref _terminalWrites, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountBlockLayout()
    {
        if (_enabled)
        {
            Interlocked.Increment(ref _blocksLaidOut);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountMaterialization()
    {
        if (_enabled)
        {
            Interlocked.Increment(ref _materializations);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountMarkdownParse()
    {
        if (_enabled)
        {
            Interlocked.Increment(ref _markdownParses);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountTerminalWrite()
    {
        if (_enabled)
        {
            Interlocked.Increment(ref _terminalWrites);
        }
    }
}

/// <summary>
///     One reading of all four <see cref="UiStageCounters" />. A record struct so
///     reading the counters allocates nothing and can be returned from a
///     benchmark without the measurement growing an <c>Allocated</c> column.
/// </summary>
/// <param name="BlocksLaidOut">See <see cref="UiStageCounters.BlocksLaidOut" />.</param>
/// <param name="Materializations">See <see cref="UiStageCounters.Materializations" />.</param>
/// <param name="MarkdownParses">See <see cref="UiStageCounters.MarkdownParses" />.</param>
/// <param name="TerminalWrites">See <see cref="UiStageCounters.TerminalWrites" />.</param>
public readonly record struct UiStageSnapshot(
    long BlocksLaidOut,
    long Materializations,
    long MarkdownParses,
    long TerminalWrites);