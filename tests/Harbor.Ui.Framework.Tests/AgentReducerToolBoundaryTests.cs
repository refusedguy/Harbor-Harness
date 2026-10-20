// AgentReducerToolBoundaryTests.cs — #1530: the tool-boundary slice of #97.
//
// Two residents, one file, the same boundary:
//
//   1. IDENTITY (upper class) — the visible text the reducer writes must stay
//      BYTE-IDENTICAL after the #1530 reshuffle. The reference oracle below is
//      the OLD two-allocation formula verbatim (`slice + "..."` then `Trim()`),
//      so "new == old" pins every edge at once: leading whitespace, a trailing
//      space INSIDE the 600-char window (where a naive `Trim()` on the window
//      would eat the space that separates the kept tail from the "..." — the
//      exact byte the old composite preserved because dots stop the end-trim),
//      an all-whitespace window, and the 599/600/601 budget edges. A couple of
//      hand-written literals double-check the oracle itself.
//
//   2. COST (lower class) — the per-unit-of-work gate over N tool-start/end
//      pairs, on the shared #1747 PerfGate: bytes per pair (machine-
//      independent, hard) and probe units per pair (machine-scaled time,
//      soft), measured over a FIXED amount of work. Since #1948 this gate is
//      NOT a ratio: the two ratio legs it used to measure were the SAME
//      measurement (`ToolBoundaryLeg(units)` on both sides), so the ratio it
//      asserted was noise around 1.00 and could not catch the quadratic
//      behaviour it was written for. The double-encode this slice removes
//      doubles the ABSOLUTE bytes per call, which is exactly what the byte
//      budget now holds.

using System.Diagnostics;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;
using Harbor.TestKit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Byte-identity of the #1530 tool-boundary reshuffle: one
///     <c>GetRawText()</c> per tool-start feeds the snapshot AND the line, one
///     preview per tool-end feeds both writers, and the visible strings are
///     what the pre-#1530 two-encode reducer wrote.
/// </summary>
public class AgentReducerToolBoundaryTests
{
    // ── the #1530 reference oracle — the OLD formula, verbatim ────────────

    /// <summary>
    ///     What <c>FormatToolResultPreview</c> produced BEFORE #1530: a
    ///     slice-and-concat, then a full <c>Trim()</c> on the composite. The
    ///     identity assertions compare the reducer's own line against this.
    /// </summary>
    private static string ReferencePreview(string output) =>
        output.Length > 600 ? output[..600] + "..." : output;

    private static string ReferenceStartLine(string toolName, string args) =>
        string.IsNullOrEmpty(args) || args == "{}"
            ? $"→ {toolName}"
            : $"→ {toolName}  {args}";

    // ── drivers ───────────────────────────────────────────────────────────

    private static UiState ReduceStart(string argsJson)
    {
        using var argsDoc = JsonDocument.Parse(argsJson);
        return AgentReducer.Reduce(
            new UiState(),
            ToolExecutionStartEvent.Create("tc1", "read", argsDoc.RootElement));
    }

    private static UiState ReducePair(string argsJson, string output, bool isError = false)
    {
        UiState state = ReduceStart(argsJson);
        // The event's THIRD positional parameter is the event's own IsError
        // (ToolCallId, Result, IsError) — it cannot be derived from the result,
        // so the error flavour must pass isError explicitly; a literal false
        // constructed success events and forced the ✓ glyph onto error lines.
        return AgentReducer.Reduce(
            state,
            new ToolExecutionEndEvent("tc1", new ToolResult(output, isError), isError));
    }

    // ── tool-start: one payload, two consumers ────────────────────────────

    [Test]
    public async Task StartLine_AndSnapshot_ShareTheRawArgs()
    {
        UiState state = ReduceStart("""{"path":"src/AgentReducer.cs","limit":42}""");

        await Assert.That(state.Chat.Lines[0].Role).IsEqualTo(ChatRole.Tool);
        await Assert.That(state.Chat.Lines[0].Text)
            .IsEqualTo(ReferenceStartLine("read", """{"path":"src/AgentReducer.cs","limit":42}"""))
            .Because(
                "the transcript line must show the raw args exactly as the old per-writer "
                + "encodings did (#1530 kept visible text byte-identical).");
        // The snapshot carries the SAME raw string the line does, so a renderer
        // never has to reparse the line to recover the call (#680).
        await Assert.That(state.Chat.ToolCalls[0].ArgsPreview)
            .IsEqualTo(state.Chat.Lines[0].Text["→ read  ".Length..])
            .Because("line and snapshot must render the same payload (#680 shows the card).");
    }

    [Test]
    public async Task StartLine_EmptyObjectArgs_AreHidden_AsBefore()
    {
        UiState object_ = ReduceStart("{}");
        await Assert.That(object_.Chat.Lines[0].Text).IsEqualTo("→ read");
        await Assert.That(object_.Chat.ToolCalls[0].ArgsPreview).IsEqualTo(string.Empty);

        // "[]" is a REAL payload to show, exactly as the old literal did: the
        // "{}" filter is an object-only hiding, not an empty-literal hiding.
        UiState array_ = ReduceStart("[]");
        await Assert.That(array_.Chat.Lines[0].Text).IsEqualTo("→ read  []");
        await Assert.That(array_.Chat.ToolCalls[0].ArgsPreview).IsEqualTo("[]");
    }

    // ── tool-end: preview identity against the old formula ────────────────

    public static string?[] OutputFixtureCases()
    {
        string _600 = new('x', 600);
        return
        [
            // empty and short
            string.Empty,
            "  ok  ",
            // budget edges: 599, exactly 600, one over
            new string('x', 599),
            _600,
            _600 + "x",
            // leading whitespace INSIDE the window must be trimmed
            "  " + new string('y', 1000),
            // a trailing space INSIDE the window must SURVIVE — dots stop the
            // old end-trim, so the gap between the kept tail and "..." stays
            new string('a', 599) + " ",
            // an all-whitespace window collapses to the bare ellipsis
            new string(' ', 700),
            // mixed: leading spaces, kept tail, appended dots
            new string(' ', 20) + "result-of-long-output " + new string('p', 900),
        ];
    }

    [Test]
    [MethodDataSource(nameof(OutputFixtureCases))]
    public async Task EndLine_MatchesTheOldPreviewFormula_ByteForByte(string output)
    {
        // Error path too: the glyph prefix is the only difference between the
        // two end-line flavours, so both assert the same reference preview.
        UiState success = ReducePair("""{"path":"a.cs"}""", output, isError: false);
        UiState error = ReducePair("""{"path":"a.cs"}""", output, isError: true);

        string expected = "✓ " + ReferencePreview(output).Trim();
        await Assert.That(success.Chat.Lines[1].Role).IsEqualTo(ChatRole.ToolResult);
        await Assert.That(success.Chat.Lines[1].Text).IsEqualTo(expected)
            .Because("transcript line must be byte-identical with the pre-#1530 reducer.");

        await Assert.That(error.Chat.Lines[1].Text)
            .IsEqualTo("✗ " + ReferencePreview(output).Trim())
            .Because("the isError glyph belongs to the line, not to the shared preview.");

        // The structured snapshot carries the preview WITHOUT the glyph (#680),
        // and it is the SAME preview both writers now share.
        await Assert.That(success.Chat.ToolCalls[0].ResultPreview)
            .IsEqualTo(ReferencePreview(output).Trim())
            .Because("the snapshot's result and the line must share one computed preview.");

        await Assert.That(success.Chat.ToolCalls[0].Status).IsEqualTo(ToolCallState.Success);
        await Assert.That(error.Chat.ToolCalls[0].Status).IsEqualTo(ToolCallState.Error);
    }

    // ── the two literal pins the oracle itself is checked against ─────────

    [Test]
    public async Task PreviewBudget_LiteralPins_TrailingWindowSpaceSurvives()
    {
        // The regression these literals exist for: truncation trims the START
        // of the window only, so a window that ends in whitespace keeps that
        // space before the "..." — and an all-space window becomes "..." alone.
        // The trailing space must sit INSIDE the 600-char window of a truncated
        // output: 599 a's + ' ' are exactly 600 chars (the SHORT branch, whose
        // Trim() legitimately drops the trailing space), so the fixture grows
        // past the budget while the window itself still ends in the space.
        UiState windowTrailingSpace = ReducePair(
            """{"path":"a.cs"}""", new string('a', 599) + " " + new string('b', 10));
        await Assert.That(windowTrailingSpace.Chat.Lines[1].Text)
            .IsEqualTo("✓ " + new string('a', 599) + " ...");

        UiState allWhitespaceWindow = ReducePair("""{"path":"a.cs"}""", new string(' ', 700));
        await Assert.That(allWhitespaceWindow.Chat.Lines[1].Text).IsEqualTo("✓ ...");
    }
}

/// <summary>
///     The per-unit-of-work cost gate for the tool boundary (#1530), on the
///     shared #1747 <c>PerfGate</c>. The unit is ONE self-contained
///     tool-start/end pair over a >2 KiB-args document and a >1 KiB output, so
///     the payloads that #1530 stopped double-encoding dominate the measured
///     bytes.
/// </summary>
[NotInParallel]
public class AgentReducerToolBoundaryScalingGates
{
    /// <summary>
    ///     Tool-start/end pairs per measured leg - the FIXED amount of work.
    ///     4 000 pairs is about 15 ms of real work on the CI runner.
    /// </summary>
    private const int PairsPerLeg = 4_000;

    /// <summary>
    ///     Allocation budget for ONE tool-start/end pair, in bytes. Machine-
    ///     independent, so it is compared as measured, with no band. Pinned
    ///     from the CI measurement of the ratio gate this replaced (run
    ///     39423151864): 208 151 840 B over 17 404 pairs = 11 959 B per pair.
    ///     The budget leaves ~1.34x headroom.
    /// </summary>
    private const long MaxBytesPerPair = 16_000;

    /// <summary>
    ///     Time budget for ONE tool-start/end pair, in PROBE UNITS (see
    ///     <see cref="PerfGate" />). Machine-scaled and SOFT. Pinned from the
    ///     same run: 63.327 ms over 17 404 pairs = 3.64 us per pair against a
    ///     probe that costs 0.000380 ms/unit, i.e. ~9.6 probe units per pair.
    ///     The budget leaves ~4.2x headroom.
    /// </summary>
    private const double MaxProbeUnitsPerPair = 40.0;

    private const string ToolName = "read";

    // >2 KiB of raw args: one GetRawText() allocation of this dominates the
    // per-unit bytes, which is what makes the info columns of this gate show
    // a double-encode as an absolute jump in docs/BENCHMARKS.md.
    private static readonly string ArgsPayloadJson =
        "{\"path\":\"/repo/src/Harbor.Ui.Framework.State/Agent/AgentReducer.cs\","
        + "\"mode\":\"read\",\"limit\":24,\"context\":\"" + new string('a', 1900) + "\"}";

    // >600 chars and leading whitespace, so each measured end-event walks the
    // truncation branch (slice + TrimStart + "..." copy) the #1530 span path
    // serves from a single allocation.
    private static readonly string OutputPayload =
        new string(' ', 24) + "result-tail " + new string('p', 1900);

    [Test]
    [Timeout(120_000)]
    public async Task ToolBoundary_Cost_GrowsSubLinearly_InEvents()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Both operands come from GC.GetAllocatedBytesForCurrentThread and
            // from a stopwatch. This project is not in ci.yml's `test-os` list,
            // so in practice this never skips in CI; it is here because the
            // guard belongs to the measurement rather than to the runner, and
            // because the sibling tripwires in this assembly carry it.
            return;
        }

        // The leg is a REPEAT of the same unit: each unit folds ONE tool pair
        // into a FRESH state, so the ImmutableArray line-copy cost stays O(1)
        // per unit and the leg stays linear. A single fold of N units into ONE
        // state would be quadratic by the transcript itself - a changed
        // problem, not a gate.
        //
        // THE GATE IS A BUDGET PER UNIT OF WORK (#1948), not a ratio. It used
        // to measure `ToolBoundaryLeg(units)` on BOTH sides and assert
        // `t(2N)/t(N) <= 3` - the same measurement twice, so the ratio was
        // noise around 1.00 and the gate could not fail on the quadratic
        // behaviour it was written for. It is now:
        //
        //     bytes per tool pair      <= 16 000   (machine-independent, HARD)
        //     probe units per tool pair <= 40       (machine-scaled time, SOFT)
        //
        // measured over a FIXED amount of work (4 000 pairs) on the SAME runner
        // in the SAME run. The allocation branch is machine-independent and is
        // asserted hard; the time branch is in PROBE UNITS and stands down when
        // the rounds do not reproduce each other. The report prints on every
        // run, pass or fail.
        PerfGateResult result = await PerfGate.MeasureAsync(
            work: units => Task.FromResult(ToolBoundaryLeg(units)),
            units: PairsPerLeg,
            options: new PerfGateOptions
            {
                Label = "tool-boundary-cost",
                MaxBytesPerUnit = MaxBytesPerPair,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerPair,
            });

        await result.AssertWithinBudgetsAsync("tool boundary fold per tool-start/end pair");

        // Non-vacuity: the measured body must have done the work it claims —
        // the payloads are above budget on purpose, so both writers' shapes
        // are visible in the produced state itself.
        using var probeArgs = JsonDocument.Parse(ArgsPayloadJson);
        ToolExecutionStartEvent probeStart = ToolExecutionStartEvent.Create(
            "tc-probe", ToolName, probeArgs.RootElement);
        ToolExecutionEndEvent probeEnd = new(
            "tc-probe", new ToolResult(OutputPayload, IsError: false), IsError: false);
        UiState probed = AgentReducer.Reduce(new UiState(), probeStart);
        probed = AgentReducer.Reduce(probed, probeEnd);

        await Assert.That(probed.Chat.ToolCalls[0].ArgsPreview).IsNotEmpty()
            .Because("the gate's unit must materialise the args payload, not fold an empty card.");
        await Assert.That(probed.Chat.ToolCalls[0].ArgsPreview.Length).IsEqualTo(ArgsPayloadJson.Length)
            .Because("the raw args payload → the snapshot verbatim; a truncated one is not this test's subject.");
        await Assert.That(probed.Chat.ToolCalls[0].ResultPreview.EndsWith("...")).IsTrue()
            .Because("the output payload exceeds the 600-char budget on purpose, so every measured "
                     + "tool-end walks the truncation branch #1530 rewrote.");
    }

    private static PerfGateLeg ToolBoundaryLeg(int units)
    {
        // Payload parse happens OUTSIDE the measured window: the leg times and
        // charges the boundary fold only. One document per leg, shared by all
        // its units — same JsonElement, same GetRawText cost per unit.
        using var argsDoc = JsonDocument.Parse(ArgsPayloadJson);
        ToolExecutionStartEvent start = ToolExecutionStartEvent.Create(
            "tc-bench", ToolName, argsDoc.RootElement);
        ToolExecutionEndEvent end = new(
            "tc-bench", new ToolResult(OutputPayload, IsError: false), IsError: false);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int u = 0; u < units; u++)
        {
            UiState state = AgentReducer.Reduce(new UiState(), start);
            state = AgentReducer.Reduce(state, end);
            // Consume the outputs so the reduce chain is not dead work.
            _ = state.Chat.Lines.Length + state.Chat.ToolCalls.Length;
        }

        double ms = (Stopwatch.GetTimestamp() - started) * 1_000.0 / Stopwatch.Frequency;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return new PerfGateLeg(ms, allocated);
    }
}
