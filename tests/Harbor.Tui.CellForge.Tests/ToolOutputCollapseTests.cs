using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// O11 #1180 (epic #1155): two-budget tool-output collapse ported from opencode
// packages/tui/src/util/collapse-tool-output.ts — head collapse with line +
// char budgets, shell tail via collapseTail, overflow flag as the expand
// affordance. Pure-helper pins plus card paint routing (bash tail vs head).
[NotInParallel("pty")]
public class ToolOutputCollapseTests
{
    [Test]
    public async Task Collapse_FitsBothBudgets_NoOverflow()
    {
        var r = ToolOutputCollapse.Collapse("a\nb", 4, 1000);
        await Assert.That(r.Overflow).IsFalse();
        await Assert.That(r.Preview).IsEqualTo("a\nb");
    }

    [Test]
    public async Task Collapse_LineOverflow_HeadPlusEllipsis()
    {
        // Mirrors the upstream test: 8 command lines + blank + 4 output lines
        // under a 10-line budget keeps the head, marks the cut inline.
        string command = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"command {i}"));
        string output = string.Join("\n", Enumerable.Range(1, 4).Select(i => $"output {i}"));
        var r = ToolOutputCollapse.Collapse($"$ {command}\n\n{output}", 10, 1000);
        await Assert.That(r.Overflow).IsTrue();
        await Assert.That(r.Preview.Split('\n').Length).IsEqualTo(10);
        await Assert.That(r.Preview.Contains("$ command 1")).IsTrue();
        await Assert.That(r.Preview.Contains("output 1…")).IsTrue();
        await Assert.That(r.Preview.Contains("output 2")).IsFalse();
    }

    [Test]
    public async Task Collapse_CharOverflow_TruncatesWithEllipsis()
    {
        var r = ToolOutputCollapse.Collapse("abcdefghij", 4, 5);
        await Assert.That(r.Overflow).IsTrue();
        await Assert.That(r.Preview).IsEqualTo("abcd…");
    }

    [Test]
    public async Task CollapseTail_KeepsLastLinesBehindLabel()
    {
        string output = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line-{i:00}"));
        string tail = ToolOutputCollapse.CollapseTail(output, 4, 1000);
        await Assert.That(tail.Contains("(7 earlier lines)")).IsTrue();
        await Assert.That(tail.Contains("line-10")).IsTrue();
        await Assert.That(tail.Contains("line-08")).IsTrue();
        await Assert.That(tail.Contains("line-01")).IsFalse();
        await Assert.That(tail.Split('\n').Length).IsEqualTo(4);
    }

    [Test]
    public async Task CollapseTail_SingleLineBudget_IsLabelOnly()
    {
        string tail = ToolOutputCollapse.CollapseTail("a\nb\nc", 1, 1000);
        await Assert.That(tail).IsEqualTo("(3 earlier lines)");
    }

    [Test]
    public async Task CollapseShell_CommandKeepsTwoLines_OutputIsTail()
    {
        string command = string.Join("\n", Enumerable.Range(1, 5).Select(i => $"cmd {i}"));
        string output = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"out-{i:00}"));
        var r = ToolOutputCollapse.CollapseShell(command, output, 10, 1000);
        await Assert.That(r.Overflow).IsTrue();
        await Assert.That(r.Output.Contains("out-10")).IsTrue();
        await Assert.That(r.Output.Contains("earlier lines")).IsTrue();
    }

    [Test]
    public async Task BashCard_Collapsed_ShowsTail_NotHead()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "bash", "make"));
        string output = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line-{i:00}"));
        block.Complete(new ToolResultBody(output, isError: false, TimeSpan.FromMilliseconds(3)));

        await Assert.That(block.UsesShellTailAtWidth(60)).IsTrue();
        await Assert.That(block.HasOverflowAtWidth(60)).IsTrue();

        var buffer = new ScreenBuffer(60, block.Measure(60).MinLines);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 60, buffer.Rows), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("earlier lines");
        await Assert.That(art).Contains("line-10");
        await Assert.That(art).DoesNotContain("line-01");
    }

    [Test]
    public async Task ReadCard_Collapsed_ShowsHead_NotTail()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "read", "f.cs")) { MaxBodyLines = 4 };
        string output = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line-{i:00}"));
        block.Complete(new ToolResultBody(output, isError: false, TimeSpan.FromMilliseconds(3)));

        await Assert.That(block.UsesShellTailAtWidth(60)).IsFalse();

        var buffer = new ScreenBuffer(60, block.Measure(60).MinLines);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 60, buffer.Rows), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("line-01");
        await Assert.That(art).Contains("…");
        await Assert.That(art).DoesNotContain("line-10");
    }

    [Test]
    public async Task ShortOutputs_HaveNoOverflow_TailOrHeadUntouched()
    {
        var bash = new ToolCallBlock(new ToolCallInfo("b1", "bash", "make"));
        bash.Complete(new ToolResultBody("ok", isError: false, TimeSpan.FromMilliseconds(1)));
        await Assert.That(bash.HasOverflowAtWidth(60)).IsFalse();
        await Assert.That(bash.UsesShellTailAtWidth(60)).IsFalse();

        var read = new ToolCallBlock(new ToolCallInfo("r1", "read", "f.cs"));
        read.Complete(new ToolResultBody("ok", isError: false, TimeSpan.FromMilliseconds(1)));
        await Assert.That(read.HasOverflowAtWidth(60)).IsFalse();
    }

    [Test]
    public async Task BashCard_Expanded_ShowsHead_FullOutput()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "bash", "make"));
        string output = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line-{i:00}"));
        block.Complete(new ToolResultBody(output, isError: false, TimeSpan.FromMilliseconds(3)));
        block.SetExpanded(true);

        var buffer = new ScreenBuffer(60, block.Measure(60).MinLines);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 60, buffer.Rows), 0));
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("line-01");
        await Assert.That(art).Contains("line-10");
        await Assert.That(art).DoesNotContain("earlier lines");
    }

    // ── #1139: the span shapes the paint path uses ──────────────────────────
    //
    // The paint/measure path asks for the OVERFLOW FLAG over a span and for
    // the tail over a span, so the whole tool body is never copied. These
    // pins are the equivalence: the span shapes answer exactly what the
    // string shapes answered.

    [Test]
    public async Task Span_Overflow_Flag_Matches_The_Collapse_Flag()
    {
        string[] samples =
        [
            string.Empty,
            "ok",
            "a\nb",
            "a\nb\nc\nd\ne\nf",
            "0123456789",
            "trailing newline\n",
        ];

        foreach (string sample in samples)
        {
            foreach (int maxLines in new[] { 1, 4, 10 })
            {
                foreach (int maxChars in new[] { 5, 100, 1000 })
                {
                    var collapsed = ToolOutputCollapse.Collapse(sample, maxLines, maxChars);
                    bool overSpan = ToolOutputCollapse.IsOverflow(sample.AsSpan(), maxLines, maxChars);
                    await Assert.That(overSpan).IsEqualTo(collapsed.Overflow)
                        .Because(
                            $"the span predicate must be the flag Collapse reports: sample {sample.Length} char(s), "
                            + $"budget {maxLines} line(s)/{maxChars} char(s).");
                }
            }
        }
    }

    [Test]
    public async Task Span_CollapseTail_Matches_The_String_Tail()
    {
        string[] samples =
        [
            string.Empty,
            "one line",
            string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line-{i:00}")),
            string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line-{i:00}")) + "\n",
        ];

        foreach (string sample in samples)
        {
            foreach (int maxLines in new[] { 1, 4, 10 })
            {
                foreach (int maxChars in new[] { 20, 100, 1000 })
                {
                    string fromString = ToolOutputCollapse.CollapseTail(sample, maxLines, maxChars);
                    string fromSpan = ToolOutputCollapse.CollapseTail(sample.AsSpan(), maxLines, maxChars);
                    await Assert.That(fromSpan).IsEqualTo(fromString)
                        .Because(
                            $"the span tail must be the string tail: sample {sample.Length} char(s), budget "
                            + $"{maxLines} line(s)/{maxChars} char(s).");
                }
            }
        }
    }
}
