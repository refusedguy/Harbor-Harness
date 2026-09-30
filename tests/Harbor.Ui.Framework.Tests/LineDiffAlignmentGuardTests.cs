using System.Collections.Immutable;
using Harbor.Abstractions.Models;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using FrameworkDiffViewModel = Harbor.Ui.Framework.ViewModels.SideBySideDiffViewModel;
using DesktopDiffViewModel = Harbor.Desktop.Abstractions.ViewModels.DiffViewModel;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Issue #679 — the guard. A diff computed BY LINE INDEX is not a diff.
/// </summary>
/// <remarks>
/// <para>
///     Both diff view-models walked <c>left[i]</c> against <c>right[i]</c> and
///     called the mismatch a modification. That is only correct when nothing
///     moves. Insert one line in the middle of a file and every line after it
///     shifts by one, so an index diff reports the whole tail of the file as
///     rewritten — a removal/addition storm in place of the single insertion
///     that actually happened. The user is shown a false description of their
///     own edit, which is a correctness bug, not a rendering preference.
/// </para>
/// <para>
///     These tests are deliberately BLACK BOX: they drive only the public
///     surface the views already bind (<c>LeftText</c>/<c>RightText</c>/
///     <c>Compute</c>/<c>Rows</c>, <c>Before</c>/<c>After</c>/<c>ComputeDiff</c>,
///     and the store-fed base view-model). The guard therefore stands on the
///     behaviour a user sees, and it does not presuppose where the algorithm
///     ends up living.
/// </para>
/// <para>
///     The third pair covers the second finding in the same perimeter:
///     <c>DiffViewModelBase</c> recognised the diff the core already produces by
///     substring-matching <c>"diff"</c>, <c>"---"</c>, <c>"+++"</c> and
///     <c>"@@"</c> anywhere in a tool line. Prose that happens to contain those
///     characters is not a diff, and the guessed path was the entire remainder
///     of the tool output rather than a path.
/// </para>
/// </remarks>
public class LineDiffAlignmentGuardTests
{
    /// <summary>Three lines, changed only by one insertion after the second.</summary>
    private const string Before = "alpha\nbravo\ncharlie";

    private const string After = "alpha\nbravo\nINSERTED\ncharlie";

    // ---------------------------------------------------------------------
    // 1. Side-by-side TEA view-model — one insertion must be ONE added row.
    // ---------------------------------------------------------------------

    [Test]
    public async Task SideBySide_MidFileInsertion_IsASingleAddedRow()
    {
        var vm = new FrameworkDiffViewModel(NullLogger<FrameworkDiffViewModel>.Instance)
        {
            LeftText = Before,
            RightText = After,
        };
        vm.ComputeCommand.Execute(null);

        var added = vm.Rows.Where(r => r.Kind == "added").ToList();

        await Assert.That(added.Count).IsEqualTo(1)
            .Because("one inserted line is one insertion, however the diff is computed");
        await Assert.That(vm.Rows.Count(r => r.Kind == "removed")).IsEqualTo(0)
            .Because("an insertion deletes nothing");
        await Assert.That(vm.Rows.Count(r => r.Kind == "modified")).IsEqualTo(0)
            .Because("an insertion shifts every following line but rewrites none of them");
        await Assert.That(added[0].Right).IsEqualTo("INSERTED");
        await Assert.That(added[0].Left).IsEqualTo(string.Empty)
            .Because("an added row has no left-hand side");
    }

    [Test]
    public async Task SideBySide_MidFileInsertion_KeepsTheTrailingLineUnchanged()
    {
        var vm = new FrameworkDiffViewModel(NullLogger<FrameworkDiffViewModel>.Instance)
        {
            LeftText = Before,
            RightText = After,
        };
        vm.ComputeCommand.Execute(null);

        // The line after the insertion point is the tell: an index diff has
        // already consumed it as the "changed" row and reports "charlie" as an
        // addition too.
        await Assert.That(vm.Rows.Count(r => r.Kind == "unchanged")).IsEqualTo(3);
        await Assert.That(vm.Rows.Any(r => r.Left == "charlie" && r.Right == "charlie")).IsTrue()
            .Because("the line after the insertion is untouched on both sides");
    }

    [Test]
    public async Task SideBySide_ReplacingALine_StillReportsAModifiedRow()
    {
        // The pairing contract the Avalonia view binds: a replaced line is one
        // row carrying both sides, not two rows.
        var vm = new FrameworkDiffViewModel(NullLogger<FrameworkDiffViewModel>.Instance)
        {
            LeftText = "alpha\nbravo\ncharlie",
            RightText = "alpha\nBRAVO\ncharlie",
        };
        vm.ComputeCommand.Execute(null);

        var modified = vm.Rows.Where(r => r.Kind == "modified").ToList();
        await Assert.That(modified.Count).IsEqualTo(1);
        await Assert.That(modified[0].Left).IsEqualTo("bravo");
        await Assert.That(modified[0].Right).IsEqualTo("BRAVO");
    }

    // ---------------------------------------------------------------------
    // 2. Desktop unified-diff view-model — one "+" line, no "-" line.
    // ---------------------------------------------------------------------

    [Test]
    public async Task Unified_MidFileInsertion_IsOnePlusLineAndNoMinusLine()
    {
        var vm = new DesktopDiffViewModel { Before = Before, After = After };
        vm.ComputeDiff();

        var lines = vm.DiffText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        await Assert.That(lines.Count(l => l.StartsWith("+ ", StringComparison.Ordinal))).IsEqualTo(1)
            .Because("one inserted line is one '+' row");
        await Assert.That(lines.Count(l => l.StartsWith("- ", StringComparison.Ordinal))).IsEqualTo(0)
            .Because("an insertion removes nothing");
        await Assert.That(vm.DiffText).Contains("+ INSERTED");
        await Assert.That(vm.DiffText).Contains("  charlie")
            .Because("the line after the insertion is context, not a change");
    }

    // ---------------------------------------------------------------------
    // 3. DiffViewModelBase — structural recognition, not substring sniffing.
    // ---------------------------------------------------------------------

    [Test]
    public async Task DiffBase_ProseThatMentionsDiffMarkers_IsNotADiff()
    {
        // Every marker the old extractor looked for is present as a SUBSTRING,
        // and none of them is a diff row: "---" and "+++" here are prose, and
        // "different" contains "diff".
        const string prose = "The result is different from the baseline\n"
                             + "--- section separator ---\n"
                             + "+++ and that is fine\n"
                             + "@@ not a hunk header @@";

        var vm = new TestDiffViewModel(new TestDispatcherAdapter(), NullLogger.Instance);
        vm.Push(ChatState(ToolResultLine(prose)));

        await Assert.That(vm.ModifiedText).IsEmpty()
            .Because("a tool result that merely mentions diff characters is not a diff");
    }

    [Test]
    public async Task DiffBase_CoreProducedContextBlock_IsRecognised()
    {
        // The shape EditTool.GenerateContextDiff emits: a prose summary line,
        // then context rows / removed rows / added rows.
        const string output = "Edited src/app.cs: 1 replacement(s) in 1 edit step(s)\n"
                              + "\n"
                              + "Diff (context):\n"
                              + "  alpha\n"
                              + "  bravo\n"
                              + "+ INSERTED\n"
                              + "  charlie";

        var vm = new TestDiffViewModel(new TestDispatcherAdapter(), NullLogger.Instance);
        vm.Push(ChatState(ToolResultLine(output)));

        await Assert.That(vm.ModifiedText).Contains("+ INSERTED");
        await Assert.That(vm.ModifiedText).Contains("  charlie");
    }

    [Test]
    public async Task DiffBase_ReadsTheFilePathOutOfAUnifiedHeader_NotTheRestOfTheOutput()
    {
        const string unified = "--- a/src/app.cs\n"
                               + "+++ b/src/app.cs\n"
                               + "@@ -1,2 +1,3 @@\n"
                               + " using System;\n"
                               + "+using System.Text;\n"
                               + " public sealed class App;";

        var vm = new TestDiffViewModel(new TestDispatcherAdapter(), NullLogger.Instance);
        vm.Push(ChatState(ToolResultLine(unified)));

        await Assert.That(vm.FilePath).IsEqualTo("src/app.cs")
            .Because("a file path is a path — the 'a/' and 'b/' prefixes are the diff's, not the file's, and the rest of the output is not a path at all");
    }

    // ---------------------------------------------------------------------

    private static ChatLine ToolResultLine(string text) =>
        new(ChatRole.ToolResult, text, ToolCallId: "tc_1");

    private static UiState ChatState(ChatLine line) =>
        new() { Chat = ChatDomainState.Empty with { Lines = ImmutableArray.Create(line) } };

    /// <summary>
    ///     Concrete <see cref="DiffViewModelBase" />: the abstract base has no
    ///     in-repo subclass, so the store feed is driven through a test double.
    /// </summary>
    private sealed class TestDiffViewModel : DiffViewModelBase
    {
        public TestDiffViewModel(IDispatcherAdapter dispatcher, ILogger logger)
            : base(dispatcher, logger)
        {
        }

        public void Push(UiState state) => OnStoreChanged(state);
    }
}
