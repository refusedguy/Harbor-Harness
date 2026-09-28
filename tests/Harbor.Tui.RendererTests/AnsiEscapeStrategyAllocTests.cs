using System.Text;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Tui.AnsiPlain.EscapeCodes;
using TUnit.Assertions;

namespace Harbor.Tui.RendererTests;

/// <summary>
///     #493: <c>AnsiEscapeStrategy.Style</c> used to rent a throwaway
///     <see cref="StringBuilder" /> and hand back <c>sb.ToString()</c> for
///     <b>every styled run</b> — per run, not per frame, so a text-heavy frame
///     paid for it many times over. The SGR parameter list for a style is now
///     read from a process-lifetime table that covers the whole six-bit
///     <c>StyleFlag</c> space, so the styled-run path allocates nothing.
/// </summary>
/// <remarks>
///     <para>
///         The gate asserts an exact zero rather than a ceiling, and
///         <c>PerStyledRunStringBuilder_StillAllocated_TripwireIsNotVacuous</c>
///         keeps the pre-#493 chain around to prove the measured zero is real
///         and not a dead sweep (the <c>SseWireDecodeAllocationTests</c>
///         convention).
///     </para>
///     <para>
///         <c>Style_MatchesTheDocumentedSgrTable_ForEveryCombination</c> is
///         the golden pin: painted bytes are a hard contract, so the whole
///         64-combination space is compared against an independently written
///         oracle, not just a sample.
///     </para>
///     <para>
///         <see cref="NotInParallelAttribute" />: same reason as
///         <c>AgentLoopAllocationTests</c> and
///         <c>StoreDispatchAllocTests</c> — the counters are thread-scoped and
///         neighbour tests move them. Min-of-3 rounds, linux-only.
///     </para>
/// </remarks>
[NotInParallel("alloc-tripwire")]
public class AnsiEscapeStrategyAllocTests
{
    /// <summary>Every <see cref="TuiStyle" /> combination (six flags, incl. <see cref="TuiStyle.None" />).</summary>
    private const int StyleCombinations = 1 << 6;

    private const int Rounds = 3;

    [Test]
    public async Task Style_SweepsEveryStyleCombination_AllocatesNothing()
    {
        if (!OperatingSystem.IsLinux())
            return; // Tripwire is linux-only: GC accounting varies across OS runtimes.

        IEscapeCodeStrategy strategy = AnsiEscapeStrategy.Instance;
        int expected = ExpectedSgrLength();

        // Warm every combination once, and prove the sweep is real: a table
        // read that returned the wrong thing would otherwise be an easy
        // accident to mistake for a zero.
        int sink = 0;
        for (int bits = 0; bits < StyleCombinations; bits++)
            sink += strategy.Style((TuiStyle)bits).Length;
        await Assert.That(sink).IsEqualTo(expected);

        // Min-of-3 rounds: a single GC/JIT hiccup must not fail the gate.
        long best = long.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            sink = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < StyleCombinations; i++)
                sink += strategy.Style((TuiStyle)i).Length;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated < best)
            {
                best = allocated;
            }
        }

        Console.WriteLine(
            $"ansi-sgr-alloc: Style() = {best} B over {StyleCombinations} style combinations x {Rounds} rounds (min of {Rounds})");
        await Assert.That(sink).IsEqualTo(expected);
        // Exact zero, not a budget: every combination is one table read now.
        // The chain the sibling test keeps spends two objects (builder + its
        // result string) per non-empty run, so any revert blows this by
        // orders of magnitude.
        await Assert.That(best).IsEqualTo(0);
    }

    [Test]
    public async Task PerStyledRunStringBuilder_StillAllocated_TripwireIsNotVacuous()
    {
        if (!OperatingSystem.IsLinux())
            return; // Tripwire is linux-only (see above).

        // The pre-#493 chain, verbatim: a throwaway StringBuilder per styled
        // run, materialized with ToString() and thrown away. It must still
        // allocate, or the exact-zero gate above could pass for the wrong
        // reason.
        int sink = 0;
        for (int bits = 0; bits < StyleCombinations; bits++)
            sink += LegacySgrParams((TuiStyle)bits).Length;

        long best = long.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            sink = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < StyleCombinations; i++)
                sink += LegacySgrParams((TuiStyle)i).Length;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated < best)
            {
                best = allocated;
            }
        }

        Console.WriteLine(
            $"ansi-sgr-alloc-legacy: per-styled-run builder = {best} B over {StyleCombinations} runs (min of {Rounds})");
        await Assert.That(sink).IsEqualTo(ExpectedSgrLength());
        // Only has to prove the legacy chain really does allocate: 63 of the 64
        // combinations built a builder and a result string, which is already
        // several hundred bytes against a StyleCombinations floor.
        await Assert.That(best).IsGreaterThan(StyleCombinations);
    }

    [Test]
    public async Task Style_MatchesTheDocumentedSgrTable_ForEveryCombination()
    {
        IEscapeCodeStrategy strategy = AnsiEscapeStrategy.Instance;

        // Spot the documented order, since a swap here is invisible in a
        // sample but repaints every bold+underline run in a terminal.
        await Assert.That(strategy.Style(TuiStyle.None)).IsEmpty();
        await Assert.That(strategy.Style(TuiStyle.Bold)).IsEqualTo("1");
        await Assert.That(strategy.Style(TuiStyle.Bold | TuiStyle.Underline)).IsEqualTo("1;4");
        await Assert.That(strategy.Style(TuiStyle.Strike | TuiStyle.Reverse)).IsEqualTo("9;7");
        await Assert.That(strategy.Style(TuiStyle.Dim | TuiStyle.Italic)).IsEqualTo("2;3");

        var mismatches = new List<string>();
        for (int bits = 0; bits < StyleCombinations; bits++)
        {
            var style = (TuiStyle)bits;
            string actual = strategy.Style(style);
            string expected = LegacySgrParams(style);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                mismatches.Add($"(TuiStyle){bits} = {style}: expected \"{expected}\", got \"{actual}\"");
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task NullStrategy_StaysEmptyForEveryStyle()
    {
        // The plain/pipes path shares the same Style() contract; collapsing to
        // the empty string must not depend on which combination is asked for.
        IEscapeCodeStrategy strategy = NullEscapeStrategy.Instance;
        var styled = new List<int>();
        for (int bits = 1; bits < StyleCombinations; bits++)
        {
            if (!string.IsNullOrEmpty(strategy.Style((TuiStyle)bits)))
                styled.Add(bits);
        }

        await Assert.That(styled).IsEmpty();
    }

    /// <summary>Total SGR parameter length across the whole style space — the sweep's own checksum.</summary>
    private static int ExpectedSgrLength()
    {
        int total = 0;
        for (int bits = 0; bits < StyleCombinations; bits++)
            total += LegacySgrParams((TuiStyle)bits).Length;
        return total;
    }

    /// <summary>
    ///     The pre-#493 <c>SgrParams</c>, verbatim: a throwaway
    ///     <see cref="StringBuilder" /> per styled run plus the
    ///     <c>ToString()</c> that materializes the result. The codes and their
    ///     order (Bold 1, Dim 2, Italic 3, Underline 4, Strike 9, Reverse 7)
    ///     are the contract the golden frames rest on, which is why this chain
    ///     doubles as the byte-identity oracle for the table.
    /// </summary>
    private static string LegacySgrParams(TuiStyle style)
    {
        if (style == TuiStyle.None)
            return string.Empty;

        StringBuilder sb = new(11);
        AppendCode(ref sb, style.HasFlag(TuiStyle.Bold), '1');
        AppendCode(ref sb, style.HasFlag(TuiStyle.Dim), '2');
        AppendCode(ref sb, style.HasFlag(TuiStyle.Italic), '3');
        AppendCode(ref sb, style.HasFlag(TuiStyle.Underline), '4');
        AppendCode(ref sb, style.HasFlag(TuiStyle.Strike), '9');
        AppendCode(ref sb, style.HasFlag(TuiStyle.Reverse), '7');
        return sb.ToString();

        static void AppendCode(ref StringBuilder sb, bool present, char code)
        {
            if (!present)
                return;
            if (sb.Length > 0)
                sb.Append(';');
            sb.Append(code);
        }
    }
}
