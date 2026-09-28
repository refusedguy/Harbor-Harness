using System.Text;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// [UX7] approval modal (#267): the fixed PRIM4 radio choice row
/// (Allow once / Allow for session / Deny) plus the PRIM4 multiline editor as
/// the reject-reason field plus a capped PRIM12 diff preview, hosted in
/// <see cref="DialogOverlay"/> on the PRIM2c z-stack — with the
/// <see cref="ApprovalGateRouter"/> modal-commit path (oldest pending gate,
/// fail-closed, Deny reason retained for audit).
/// Pure state transitions plus paint smoke — no backends.
/// </summary>
// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class DialogApprovalTests
{
    private static readonly ConsoleKeyInfo EnterKey = new('\r', ConsoleKey.Enter, false, false, false);
    private static readonly ConsoleKeyInfo EscKey = new('\x1b', ConsoleKey.Escape, false, false, false);

    // DialogOverlay approval editing only inspects Key for navigation —
    // any other Key value types KeyChar verbatim into the reason field.
    private static ConsoleKeyInfo CharKey(char c) => new(c, ConsoleKey.A, false, false, false);

    private static ConsoleKeyInfo SpecialKey(ConsoleKey key) => new('\0', key, false, false, false);

    private static void Type(DialogOverlay dialog, string text)
    {
        foreach (char c in text)
        {
            _ = dialog.HandleKey(CharKey(c));
        }
    }

    private static string PaintArt(DialogOverlay dialog, int cols = 64, int rows = 22)
    {
        var viewport = new Rect(0, 0, cols, rows);
        var buffer = new ScreenBuffer(cols, rows);
        dialog.Paint(buffer, viewport);
        return GridDump.Art(buffer);
    }

    private static ApprovalGateRouter MakeRouter(out ChatTimelinePanel panel)
    {
        panel = new ChatTimelinePanel("chat", 40, 6);
        return new ApprovalGateRouter(panel, new StatusViewModel());
    }

    [Test]
    public async Task ShowApproval_State_SeedsFixedChoices_ClampsIndex_CoercesTool()
    {
        var dialog = new DialogOverlay();
        dialog.ShowApproval("  ", "run rm?", selectedIndex: 9);

        await Assert.That(dialog.Kind).IsEqualTo(DialogKind.Approval);
        await Assert.That(dialog.Visible).IsTrue();
        await Assert.That(dialog.ApprovalTool).IsEqualTo("?");
        await Assert.That(dialog.Options.Count).IsEqualTo(3);
        await Assert.That(dialog.SelectedIndex).IsEqualTo(2);
        await Assert.That(dialog.SelectedOption).IsEqualTo("Deny");
        await Assert.That(dialog.SelectedApproval).IsEqualTo(ApprovalChoice.Deny);
        await Assert.That(dialog.RejectReason).IsEqualTo(string.Empty);
        await Assert.That(dialog.ApprovalDiff.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ShowApproval_Diff_ParsedCapped_Priml12Rules()
    {
        var dialog = new DialogOverlay();
        dialog.ShowApproval(
            "edit",
            "patch main.cs",
            "--- a/main.cs\n+++ b/main.cs\n@@ -1,2 +1,2 @@\n-gone\n+added-line\n context\n\\ No newline\n+extra1\n+extra2\n+extra3\n+extra4",
            "main.cs");

        // Preamble skipped, rows capped at MaxApprovalDiffRows, kinds classified.
        await Assert.That(dialog.ApprovalDiff.Count).IsEqualTo(DialogOverlay.MaxApprovalDiffRows);
        await Assert.That(dialog.ApprovalDiff[0].Kind).IsEqualTo(DiffViewerLineKind.Context);
        await Assert.That(dialog.ApprovalDiff[1].Kind).IsEqualTo(DiffViewerLineKind.Removed);
        await Assert.That(dialog.ApprovalDiff[1].Text).IsEqualTo("gone");
        await Assert.That(dialog.ApprovalDiff[2].Kind).IsEqualTo(DiffViewerLineKind.Added);
        await Assert.That(dialog.ApprovalDiff[2].Text).IsEqualTo("added-line");
        await Assert.That(dialog.ApprovalFilePath).IsEqualTo("main.cs");
    }

    [Test]
    public async Task SelectedApproval_MapsRadioIndex_AllowSessionDeny()
    {
        var dialog = new DialogOverlay();
        dialog.ShowApproval("bash", "rm -rf /", selectedIndex: 0);
        await Assert.That(dialog.SelectedApproval).IsEqualTo(ApprovalChoice.Approve);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.RightArrow));
        await Assert.That(dialog.SelectedApproval).IsEqualTo(ApprovalChoice.AlwaysAllow);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.RightArrow));
        await Assert.That(dialog.SelectedApproval).IsEqualTo(ApprovalChoice.Deny);

        // Wrap-around (radio semantics): one more step returns to allow-once.
        _ = dialog.HandleKey(SpecialKey(ConsoleKey.RightArrow));
        await Assert.That(dialog.SelectedApproval).IsEqualTo(ApprovalChoice.Approve);

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.UpArrow));
        await Assert.That(dialog.SelectedApproval).IsEqualTo(ApprovalChoice.Deny);
    }

    [Test]
    public async Task Approval_Typing_EditsReason_EnterSubmits_EscapeDismisses()
    {
        var dialog = new DialogOverlay();
        dialog.ShowApproval("bash", "rm -rf /");

        // Arrows stay on the choice row; printable keys feed the reason field.
        _ = dialog.HandleKey(SpecialKey(ConsoleKey.DownArrow));
        await Assert.That(dialog.SelectedIndex).IsEqualTo(1);

        Type(dialog, "no");
        await Assert.That(dialog.RejectReason).IsEqualTo("no");

        _ = dialog.HandleKey(SpecialKey(ConsoleKey.Backspace));
        await Assert.That(dialog.RejectReason).IsEqualTo("n");

        await Assert.That(dialog.HandleKey(EnterKey)).IsFalse();
        await Assert.That(dialog.Visible).IsTrue();

        await Assert.That(dialog.HandleKey(EscKey)).IsTrue();
        await Assert.That(dialog.Visible).IsFalse();
    }

    [Test]
    public async Task Approval_KeyEvent_CharInserts_NavMoves_ShiftEnterNewline()
    {
        var dialog = new DialogOverlay();
        dialog.ShowApproval("edit", "patch");

        await Assert.That(dialog.HandleKey(KeyEvent.Char(new Rune('x')))).IsTrue();
        await Assert.That(dialog.RejectReason).IsEqualTo("x");

        await Assert.That(dialog.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(dialog.SelectedIndex).IsEqualTo(1);

        bool newline = dialog.HandleKey(KeyEvent.Simple(KeyCode.Enter, KeyModifiers.Shift, isKittyEncoded: true));
        await Assert.That(newline).IsTrue();
        await Assert.That(dialog.Editor.LineCount).IsEqualTo(2);

        await Assert.That(dialog.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();
    }

    [Test]
    public async Task Approval_Paint_ContainsChoices_Diff_File_AndReasonCaption()
    {
        var dialog = new DialogOverlay();
        dialog.ShowApproval("edit", "patch main.cs", "-gone\n+added-line", "main.cs");
        Type(dialog, "why");

        string art = PaintArt(dialog);

        await Assert.That(art.Contains("Allow once")).IsTrue();
        await Assert.That(art.Contains("Allow for session")).IsTrue();
        await Assert.That(art.Contains("Deny")).IsTrue();
        await Assert.That(art.Contains("- gone")).IsTrue();
        await Assert.That(art.Contains("+ added-line")).IsTrue();
        await Assert.That(art.Contains("main.cs")).IsTrue();
        await Assert.That(art.Contains("reject reason")).IsTrue();
        await Assert.That(art.Contains("why")).IsTrue();
    }

    [Test]
    public async Task Router_CommitModalDecision_Allow_DecidesOldestGate()
    {
        var router = MakeRouter(out _);
        var first = router.BeginApprovalGate("bash", "rm -rf /");
        var second = router.BeginApprovalGate("edit", "patch");

        await Assert.That(router.CommitModalDecision(ApprovalChoice.Approve)).IsTrue();
        await Assert.That(first.Decision).IsEqualTo(ApprovalChoice.Approve);
        await Assert.That(second.IsPending).IsTrue();
        await Assert.That(router.GetRejectReason(first.Id)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Router_CommitModalDecision_Deny_RecordsTrimmedReason()
    {
        var router = MakeRouter(out _);
        var gate = router.BeginApprovalGate("bash", "rm -rf /");

        await Assert.That(router.CommitModalDecision(ApprovalChoice.Deny, "  too risky  ")).IsTrue();
        await Assert.That(gate.Decision).IsEqualTo(ApprovalChoice.Deny);
        await Assert.That(router.GetRejectReason(gate.Id)).IsEqualTo("too risky");
    }

    [Test]
    public async Task Router_CommitModalDecision_None_FailsClosedToDeny()
    {
        var router = MakeRouter(out _);
        var gate = router.BeginApprovalGate("bash", "rm -rf /");

        await Assert.That(router.CommitModalDecision(ApprovalChoice.None, "ignored")).IsTrue();
        await Assert.That(gate.Decision).IsEqualTo(ApprovalChoice.Deny);
        await Assert.That(router.GetRejectReason(gate.Id)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Router_CommitModalDecision_NoGate_ReturnsFalse()
    {
        var router = MakeRouter(out _);

        await Assert.That(router.CommitModalDecision(ApprovalChoice.Approve)).IsFalse();
        await Assert.That(router.GetRejectReason("nope")).IsEqualTo(string.Empty);
        await Assert.That(router.GetRejectReason(null)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Modal_EndToEnd_DenyWithReason_LandsOnGate()
    {
        // Host contract: show the modal, route keys through the dialog, commit
        // the dialog's choice + reason into the oldest pending gate.
        var router = MakeRouter(out _);
        var gate = router.BeginApprovalGate("bash", "rm -rf /");

        var dialog = new DialogOverlay();
        dialog.ShowApproval(gate.ToolName, "run rm?", "- rm -rf /\n+ echo safe", "run.sh");
        _ = dialog.HandleKey(SpecialKey(ConsoleKey.End)); // Deny
        Type(dialog, "destructive");

        await Assert.That(dialog.HandleKey(EnterKey)).IsFalse();
        await Assert.That(router.CommitModalDecision(dialog.SelectedApproval, dialog.RejectReason)).IsTrue();
        await Assert.That(gate.Decision).IsEqualTo(ApprovalChoice.Deny);
        await Assert.That(router.GetRejectReason(gate.Id)).IsEqualTo("destructive");
    }
}
