using System.Text;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

public class ApprovalGateViewTests
{
    private static readonly KeyEvent Y = KeyEvent.Char(new Rune('y'));
    private static readonly KeyEvent N = KeyEvent.Char(new Rune('n'));
    private static readonly KeyEvent A = KeyEvent.Char(new Rune('a'));

    [Test]
    public async Task Measure_FixedHeight_ThreePlusDetailRows()
    {
        var block = new ApprovalGateView("bash", "rm -rf ./build");

        // header + one detail row + hint row
        await Assert.That(block.Measure(60).MinLines).IsEqualTo(3);
        await Assert.That(block.Measure(60).IsExact).IsTrue();

        var wrapped = new ApprovalGateView("bash", "arg one arg two");
        int rows = 2 + new ApprovalGateView("bash", "x").WrappedDetail(20).Count; // sanity reference
        _ = rows;
        await Assert.That(wrapped.WrappedDetail(8).Count).IsGreaterThan(1);
        await Assert.That(wrapped.Measure(8).MinLines).IsEqualTo(wrapped.WrappedDetail(8).Count + 2);
    }

    [Test]
    public async Task Measure_HeightIdentical_AfterDecision()
    {
        var block = new ApprovalGateView("edit", "src/app.cs");
        int before = block.Measure(40).BestGuess;

        _ = block.HandleKey(Y);
        await Assert.That(block.IsPending).IsFalse();
        await Assert.That(block.Measure(40).BestGuess).IsEqualTo(before);
    }

    [Test]
    public async Task HandleKey_Maps_YesNoAlways()
    {
        var approve = new ApprovalGateView("bash", "ls -la");
        await Assert.That(approve.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(approve.Decision).IsEqualTo(ApprovalChoice.Approve);

        var deny = new ApprovalGateView("bash", "ls -la");
        await Assert.That(deny.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsTrue();
        await Assert.That(deny.Decision).IsEqualTo(ApprovalChoice.Deny);

        var always = new ApprovalGateView("bash", "ls -la");
        await Assert.That(always.HandleKey(A)).IsTrue();
        await Assert.That(always.Decision).IsEqualTo(ApprovalChoice.AlwaysAllow);
    }

    [Test]
    public async Task HandleKey_CaseInsensitive_AndIgnoresAfterDecision()
    {
        var block = new ApprovalGateView("read", "a.txt");
        await Assert.That(block.HandleKey(KeyEvent.Char(new Rune('N')))).IsTrue();
        await Assert.That(block.Decision).IsEqualTo(ApprovalChoice.Deny);

        // Resolved gate stops consuming keys.
        await Assert.That(block.HandleKey(Y)).IsFalse();

        var pending = new ApprovalGateView("read", "a.txt");
        await Assert.That(pending.HandleKey(KeyEvent.Char(new Rune('z')))).IsFalse();
        await Assert.That(pending.HandleKey(KeyEvent.Char(new Rune('c'), KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(pending.IsPending).IsTrue();
    }

    [Test]
    public async Task Paint_ShowsHeader_Detail_AndHint()
    {
        var buffer = new ScreenBuffer(50, 3);
        var block = new ApprovalGateView("bash", "cargo build --release");
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 50, 3), 0));

        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("permission required · bash");
        await Assert.That(art).Contains("cargo build --release");
        await Assert.That(art).Contains("[y] approve   [n] deny   [a] always allow");
    }

    [Test]
    public async Task Paint_Decided_Gate_StampsOutcome()
    {
        var ok = new ApprovalGateView("edit", "f.cs");
        _ = ok.HandleKey(A);
        var bufOk = new ScreenBuffer(30, 3);
        ok.Paint(new BlockPaintContext(bufOk, new Rect(0, 0, 30, 3), 0));
        await Assert.That(GridDump.Art(bufOk)).Contains("approved (always)");

        var denied = new ApprovalGateView("edit", "f.cs");
        _ = denied.HandleKey(N);
        var bufNo = new ScreenBuffer(30, 3);
        denied.Paint(new BlockPaintContext(bufNo, new Rect(0, 0, 30, 3), 0));
        await Assert.That(GridDump.Art(bufNo)).Contains("denied");
    }
}
