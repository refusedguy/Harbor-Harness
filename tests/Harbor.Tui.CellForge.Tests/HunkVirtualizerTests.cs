using Harbor.Tui.CellForge.Panels;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     O5 hunk-virtualized diff (epic #1155, issue #1174 — port of opencode
///     <c>util/diff.ts splitPatchHunks</c> + <c>patch-diff.tsx</c>): thresholds
///     3000/128, added-only split with own <c>@@</c> headers, full-file gutter,
///     split/unified auto by width &gt; 120.
/// </summary>
public class HunkVirtualizerTests
{
    private static string AddedPatch(int addedLines, int newStart = 1)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("--- a/f.cs\n+++ b/f.cs\n");
        sb.Append($"@@ -1,3 +{newStart},{addedLines + 3} @@\n");
        sb.Append(" context\n");
        for (int i = 0; i < addedLines; i++)
        {
            sb.Append("+added ").Append(i).Append('\n');
        }

        sb.Append(" context\n");
        sb.Append(" context\n");
        return sb.ToString();
    }

    [Test]
    public async Task Thresholds_Match_Opencode_Port()
    {
        // The three thresholds ARE constants, and the constants ARE the
        // subject: this test pins the numbers the port was measured at. A
        // change to a constant recompiles these assertions to the new value
        // and the test fails, so the drift is noticed. TUnit's
        // TUnitAssertions0005 reports the constant first argument; see the
        // policy in tests/Directory.Build.props.
#pragma warning disable TUnitAssertions0005 // the virtualizer threshold constants are the subject: the test pins their numeric values
        await Assert.That(HunkVirtualizer.VirtualMinLines).IsEqualTo(3000);
        await Assert.That(HunkVirtualizer.VirtualChunkLines).IsEqualTo(128);
        await Assert.That(HunkVirtualizer.SplitWidthThreshold).IsEqualTo(120);
#pragma warning restore TUnitAssertions0005
    }

    [Test]
    public async Task NeedsVirtualization_Boundary()
    {
        await Assert.That(HunkVirtualizer.NeedsVirtualization(2999)).IsFalse();
        await Assert.That(HunkVirtualizer.NeedsVirtualization(3000)).IsTrue();
        await Assert.That(HunkVirtualizer.NeedsVirtualization(3001)).IsTrue();
    }

    [Test]
    public async Task PreferSplit_Boundary()
    {
        await Assert.That(HunkVirtualizer.PreferSplit(120)).IsFalse();
        await Assert.That(HunkVirtualizer.PreferSplit(121)).IsTrue();
        await Assert.That(HunkVirtualizer.PreferSplit(80)).IsFalse();
    }

    [Test]
    public async Task GutterWidth_Decimal_Digits()
    {
        await Assert.That(HunkVirtualizer.GutterWidth(1)).IsEqualTo(1);
        await Assert.That(HunkVirtualizer.GutterWidth(9)).IsEqualTo(1);
        await Assert.That(HunkVirtualizer.GutterWidth(10)).IsEqualTo(2);
        await Assert.That(HunkVirtualizer.GutterWidth(99)).IsEqualTo(2);
        await Assert.That(HunkVirtualizer.GutterWidth(100)).IsEqualTo(3);
        await Assert.That(HunkVirtualizer.GutterWidth(3000)).IsEqualTo(4);
    }

    [Test]
    public async Task Short_Added_Run_Passes_Through_With_Original_Header()
    {
        string patch = AddedPatch(10);
        VirtualPatch parsed = HunkVirtualizer.SplitPatchHunks(patch);
        await Assert.That(parsed.WasChunked).IsFalse();
        await Assert.That(parsed.Hunks.Count).IsEqualTo(1);
        await Assert.That(parsed.Hunks[0].IsVirtualChunk).IsFalse();
        await Assert.That(parsed.Hunks[0].Header).Contains("@@");
        await Assert.That(parsed.TotalLines).IsEqualTo(13);
    }

    [Test]
    public async Task Added_Only_Long_Run_Splits_Into_128_Line_Chunks()
    {
        string patch = AddedPatch(300);
        VirtualPatch parsed = HunkVirtualizer.SplitPatchHunks(patch);
        await Assert.That(parsed.WasChunked).IsTrue();
        int chunked = 0;
        int chunkedLines = 0;
        foreach (VirtualHunk hunk in parsed.Hunks)
        {
            if (hunk.IsVirtualChunk)
            {
                chunked++;
                chunkedLines += hunk.Lines.Count;
                await Assert.That(hunk.Lines.Count <= HunkVirtualizer.VirtualChunkLines).IsTrue();
                await Assert.That(hunk.Header).Contains("@@");
                await Assert.That(hunk.Header).Contains("chunk");
            }
        }

        await Assert.That(chunked).IsEqualTo(3);
        await Assert.That(chunkedLines).IsEqualTo(300);
        await Assert.That(parsed.TotalLines).IsEqualTo(303);
    }

    [Test]
    public async Task Mixed_Run_With_Removed_Lines_Never_Cut()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("--- a/f.cs\n+++ b/f.cs\n@@ -1,5 +1,5 @@\n");
        for (int i = 0; i < 200; i++)
        {
            sb.Append(i % 2 == 0 ? "+added\n" : "-removed\n");
        }

        VirtualPatch parsed = HunkVirtualizer.SplitPatchHunks(sb.ToString());
        await Assert.That(parsed.WasChunked).IsFalse();
        await Assert.That(parsed.Hunks.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Gutter_Anchors_To_Full_File_Max_New_Line()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("--- a/f.cs\n+++ b/f.cs\n");
        sb.Append("@@ -1,3 +1,3 @@\n context\n context\n context\n");
        sb.Append("@@ -1,3 +95,3 @@\n context\n context\n context\n");
        VirtualPatch parsed = HunkVirtualizer.SplitPatchHunks(sb.ToString());
        await Assert.That(parsed.MaxNewLine >= 97).IsTrue();
        await Assert.That(HunkVirtualizer.GutterWidth(parsed.MaxNewLine)).IsEqualTo(2);
    }

    [Test]
    public async Task Crlf_Normalized_Up_Front()
    {
        string patch = "--- a/f.cs\r\n+++ b/f.cs\r\n@@ -1,2 +1,2 @@\r\n context\r\n+added\r\n";
        VirtualPatch parsed = HunkVirtualizer.SplitPatchHunks(patch);
        await Assert.That(parsed.Hunks.Count).IsEqualTo(1);
        await Assert.That(parsed.TotalLines).IsEqualTo(2);
        foreach (string line in parsed.Hunks[0].Lines)
        {
            await Assert.That(line).DoesNotContain("\r");
        }
    }

    [Test]
    public async Task Headerless_Body_Falls_Back_To_Single_Synthesized_Hunk()
    {
        VirtualPatch parsed = HunkVirtualizer.SplitPatchHunks("+added line\n-removed line\n context");
        await Assert.That(parsed.Hunks.Count).IsEqualTo(1);
        await Assert.That(parsed.WasChunked).IsFalse();
        await Assert.That(parsed.TotalLines).IsEqualTo(3);
    }

    [Test]
    public async Task Null_And_Empty_Yield_Empty_Patch()
    {
        await Assert.That(HunkVirtualizer.SplitPatchHunks(null).Hunks.Count).IsEqualTo(0);
        await Assert.That(HunkVirtualizer.SplitPatchHunks(string.Empty).Hunks.Count).IsEqualTo(0);
        await Assert.That(HunkVirtualizer.SplitPatchHunks(null).WasChunked).IsFalse();
    }

    /// <summary>
    ///     #1139: the split walks the body in place — the breaks are found
    ///     where they are (CRLF is ONE break, a lone CR is one too) and a
    ///     line's tabs are expanded when the line is materialized. The lines
    ///     are the same ones the old normalize-then-split-then-replace chain
    ///     produced, and the trailing empty line is still not a line.
    /// </summary>
    [Test]
    public async Task Lone_Cr_Is_One_Break_And_Tabs_Expand_To_Two_Spaces()
    {
        string patch = "--- a/f.cs\r@@ -1,2 +1,2 @@\n\tcontext\n+added\twith\ttabs\n";
        VirtualPatch parsed = HunkVirtualizer.SplitPatchHunks(patch);

        await Assert.That(parsed.Preamble.Count).IsEqualTo(1);
        await Assert.That(parsed.Preamble[0]).IsEqualTo("--- a/f.cs");
        await Assert.That(parsed.Hunks.Count).IsEqualTo(1);
        await Assert.That(parsed.Hunks[0].Lines.Count).IsEqualTo(2);
        await Assert.That(parsed.Hunks[0].Lines[0]).IsEqualTo("  context");
        await Assert.That(parsed.Hunks[0].Lines[1]).IsEqualTo("+added  with  tabs");
        await Assert.That(parsed.TotalLines).IsEqualTo(2);
    }

    /// <summary>
    ///     #1139: a body whose LAST line is empty drops that line (the old
    ///     shape dropped the trailing element of the split array), while an
    ///     empty line in the middle stays a line.
    /// </summary>
    [Test]
    public async Task Trailing_Empty_Line_Is_Not_A_Line_Middle_Empty_Lines_Are()
    {
        VirtualPatch trailing = HunkVirtualizer.SplitPatchHunks("--- a/f.cs\n@@ -1,1 +1,1 @@\n context\n");
        await Assert.That(trailing.Hunks.Count).IsEqualTo(1);
        await Assert.That(trailing.Hunks[0].Lines.Count).IsEqualTo(1);
        await Assert.That(trailing.TotalLines).IsEqualTo(1);

        VirtualPatch middle = HunkVirtualizer.SplitPatchHunks("--- a/f.cs\n@@ -1,2 +1,2 @@\n\n context\n");
        await Assert.That(middle.Hunks.Count).IsEqualTo(1);
        await Assert.That(middle.Hunks[0].Lines.Count).IsEqualTo(2);
        await Assert.That(middle.TotalLines).IsEqualTo(2);
    }

    private static PanelContext Ctx(UiState state, int width = 80, int height = 24) =>
        new(state, width, height, null);

    private static UiState StateWithLines(params ChatLine[] lines) =>
        new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Lines = ImmutableArray.Create(lines)
            }
        };

    private static string Joined(object? widget) => widget switch
    {
        null => string.Empty,
        string s => s,
        IReadOnlyList<string> rows => string.Join("\n", rows),
        IEnumerable<string> lines => string.Join("\n", lines),
        _ => widget.ToString() ?? string.Empty,
    };

    [Test]
    public async Task Panel_Title_Carries_Split_Unified_Auto_Mode()
    {
        var state = StateWithLines(
            new ChatLine(ChatRole.Tool, "→ edit {\"path\": \"src/a.cs\"}", "tc1"),
            new ChatLine(ChatRole.ToolResult, "✓ +added line", "tc1"));
        string narrow = Joined(new CellForgeDiffPreviewPanel().Build(Ctx(state, width: 80)));
        string wide = Joined(new CellForgeDiffPreviewPanel().Build(Ctx(state, width: 200)));
        await Assert.That(narrow).Contains("unified");
        await Assert.That(wide).Contains("split");
        await Assert.That(narrow).Contains("+added line");
    }

    [Test]
    public async Task Panel_Virtualization_Marker_For_Large_Body()
    {
        var sb = new System.Text.StringBuilder("✓\n");
        for (int i = 0; i < 3100; i++)
        {
            sb.Append("+added ").Append(i).Append('\n');
        }

        var state = StateWithLines(
            new ChatLine(ChatRole.Tool, "→ edit {\"path\": \"src/big.cs\"}", "tc1"),
            new ChatLine(ChatRole.ToolResult, sb.ToString(), "tc1"));
        string text = Joined(new CellForgeDiffPreviewPanel().Build(Ctx(state)));
        await Assert.That(text).Contains("virtualized");
        await Assert.That(text).Contains("3100");
    }
}
