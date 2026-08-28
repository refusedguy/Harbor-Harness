using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

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

    [Test]
    public async Task DecisionRecorded_Fires_Once_PerGate()
    {
        var gate = new ApprovalGateView("bash", "ls");
        int fired = 0;
        gate.DecisionRecorded += (_, _) => fired++;

        await Assert.That(gate.HandleKey(Y)).IsTrue();
        await Assert.That(fired).IsEqualTo(1);

        // Decided gates swallow keys — no second signal, no stamp rewrite.
        await Assert.That(gate.HandleKey(N)).IsFalse();
        await Assert.That(fired).IsEqualTo(1);
        await Assert.That(gate.Decision).IsEqualTo(ApprovalChoice.Approve);
    }

    private static (ApprovalGateView Gate, int HintRow) PaintedGate(int width)
    {
        var gate = new ApprovalGateView("bash", "cargo build --release");
        var buf = new ScreenBuffer(width, 8);
        gate.Paint(new BlockPaintContext(buf, new Rect(0, 0, width, 3), 0));
        // header row 0 + one detail row 1 + hint/stamp row 2
        return (gate, 2);
    }

    [Test]
    public async Task TryHitDecision_Maps_ButtonZones()
    {
        var (gate, hintRow) = PaintedGate(60);

        await Assert.That(gate.TryHitDecision(0, hintRow)).IsEqualTo(ApprovalChoice.Approve);      // "[y]"
        await Assert.That(gate.TryHitDecision(5, hintRow)).IsEqualTo(ApprovalChoice.Approve);      // inside "approve"
        await Assert.That(gate.TryHitDecision(14, hintRow)).IsEqualTo(ApprovalChoice.Deny);        // "[n]"
        await Assert.That(gate.TryHitDecision(19, hintRow)).IsEqualTo(ApprovalChoice.Deny);
        await Assert.That(gate.TryHitDecision(27, hintRow)).IsEqualTo(ApprovalChoice.AlwaysAllow); // "[a] …"
        await Assert.That(gate.TryHitDecision(45, hintRow)).IsNull();                              // past label tail
    }

    [Test]
    public async Task TryHitDecision_Ignores_OtherRows_AndResolvedGates()
    {
        var (gate, hintRow) = PaintedGate(50);
        await Assert.That(gate.TryHitDecision(0, 0)).IsNull();              // header
        await Assert.That(gate.TryHitDecision(0, 1)).IsNull();              // detail
        await Assert.That(gate.TryHitDecision(0, hintRow + 1)).IsNull();    // below card

        _ = gate.HandleKey(Y);
        await Assert.That(gate.IsPending).IsFalse();
        await Assert.That(gate.TryHitDecision(1, hintRow)).IsNull();
    }

    [Test]
    public async Task TryHitDecision_BeforeFirstPaint_ReturnsNull()
    {
        var fresh = new ApprovalGateView("bash", "ls");
        await Assert.That(fresh.TryHitDecision(1, 2)).IsNull();
    }

    [Test]
    public async Task Id_IsUniquePerInstance_ForSameToolGates()
    {
        var a = new ApprovalGateView("bash", "one");
        var b = new ApprovalGateView("bash", "two");

        await Assert.That(a.Id).IsNotEqualTo(b.Id);
        await Assert.That(a.Id).Contains("bash");
        await Assert.That(b.Id).Contains("bash");
        await Assert.That(a.Id).IsEqualTo(a.Id);
    }

    [Test]
    public async Task TryDecide_ClickPath_OneShot_AndRejectsNone()
    {
        var (gate, hintRow) = PaintedGate(60);
        var choice = gate.TryHitDecision(1, hintRow);
        await Assert.That(choice).IsEqualTo(ApprovalChoice.Approve);

        int fired = 0;
        gate.DecisionRecorded += (_, _) => fired++;
        await Assert.That(gate.TryDecide(choice!.Value)).IsTrue();
        await Assert.That(fired).IsEqualTo(1);

        // Second click / second TryDecide cannot rewrite the audit stamp.
        await Assert.That(gate.TryDecide(ApprovalChoice.Deny)).IsFalse();
        await Assert.That(fired).IsEqualTo(1);
        await Assert.That(gate.Decision).IsEqualTo(ApprovalChoice.Approve);
        await Assert.That(gate.TryDecide(ApprovalChoice.None)).IsFalse();
    }
}
