using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

public class StatusSegmentBarTests
{
    private static StatusViewModel Vm(StatusBarMode mode = StatusBarMode.Idle) => new()
    {
        Model = "kilocode/hy3",
        Mode = mode,
    };

    [Test]
    public async Task BuildSegments_Order_Model_Ctx_Tokens_Cost()
    {
        var vm = Vm();
        vm.SetContext(3000, 10_000);
        vm.SetUsage(12_000, 4_500, 0.0031m);

        var ws = new StatusSeg[8];
        int n = vm.BuildSegments(ws);

        await Assert.That(n).IsEqualTo(4);
        await Assert.That(ws[0].Text).IsEqualTo("kilocode/hy3");
        await Assert.That(ws[0].FixedPriority).IsTrue();
        await Assert.That(ws[1].Text).IsEqualTo("▰▰▱▱▱▱"); // 30% of 6 cells → 2
        await Assert.That(ws[2].Text).IsEqualTo("12k↑ 4.5k↓");
        await Assert.That(ws[3].Text).IsEqualTo("$0.0031");
    }

    [Test]
    public async Task Context_None_Semantics_SegmentAbsentNotZero()
    {
        var vm = Vm(); // no SetContext
        var ws = new StatusSeg[8];
        int n = vm.BuildSegments(ws);

        await Assert.That(vm.TryGetContextTokens(out _)).IsFalse();
        await Assert.That(n).IsEqualTo(1); // only model
    }

    [Test]
    public async Task ContextBar_Thresholds_50_and_85_Percent()
    {
        var vm = Vm();
        vm.SetContext(5_100, 10_000);
        var ws = new StatusSeg[8];
        _ = vm.BuildSegments(ws);
        await Assert.That(ws[1].Accent).IsEqualTo(StatusAccent.Warning); // 51% ≥ 50

        vm.SetContext(8_600, 10_000);
        _ = vm.BuildSegments(ws);
        await Assert.That(ws[1].Accent).IsEqualTo(StatusAccent.Error);   // 86% ≥ 85
        await Assert.That(ws[1].Text).IsEqualTo("▰▰▰▰▰▱");

        vm.SetContext(1_000, 10_000);
        _ = vm.BuildSegments(ws);
        await Assert.That(ws[1].Accent).IsEqualTo(StatusAccent.Success);

        await Assert.That(StatusViewModel.ContextBar(0.49)).IsEqualTo("▰▰▰▱▱▱"); // 2.94 → 3
        await Assert.That(StatusViewModel.ContextBar(0.25)).IsEqualTo("▰▰▱▱▱▱"); // 1.5 → 2
        await Assert.That(StatusViewModel.ContextBar(0.0)).IsEqualTo("▱▱▱▱▱▱");
        await Assert.That(StatusViewModel.ContextBar(1.0)).IsEqualTo("▰▰▰▰▰▰");
    }

    [Test]
    [Arguments(20)]
    [Arguments(40)]
    [Arguments(80)]
    [Arguments(120)]
    public async Task Fit_ModelAlwaysSurvives_FlexibleCutRightFirst(int width)
    {
        var vm = Vm();
        vm.SetContext(9_000, 10_000);
        vm.SetUsage(120_000, 45_000, 0.1234m);

        var ws = new StatusSeg[8];
        int n = vm.BuildSegments(ws);
        Span<StatusSeg> span = ws;
        int kept = StatusBarLayout.Fit(span[..n], width);

        string modelText = ws[0].Text;
        bool modelFixed = ws[0].FixedPriority;
        string secondText = kept >= 2 ? ws[1].Text : "";

        await Assert.That(kept).IsGreaterThanOrEqualTo(1);
        await Assert.That(modelText).IsEqualTo("kilocode/hy3");   // fixed survives everywhere
        await Assert.That(modelFixed).IsTrue();
        if (width >= 40)
        {
            await Assert.That(kept).IsGreaterThanOrEqualTo(2);     // ctx bar fits from 40 up
            await Assert.That(secondText).Contains("▰");
        }
    }

    [Test]
    public async Task Fit_NarrowRow_TruncatesEvenFixed_AsLastResort()
    {
        var ws = new StatusSeg[]
        {
            new("longmodelname/withsuffix", StatusAccent.Accent, FixedPriority: true),
            new("⏸ awaiting approval", StatusAccent.Warning, FixedPriority: true),
        };
        int kept = StatusBarLayout.Fit(ws, 18);
        int totalWidth = StatusBarLayout.TotalWidth(ws.AsSpan()[..kept]);

        await Assert.That(kept).IsEqualTo(2);
        await Assert.That(totalWidth <= 18).IsTrue();
    }

    [Test]
    public async Task Fit_DropsCostBeforeTokensBeforeCtx()
    {
        var ws = new StatusSeg[]
        {
            new("model", StatusAccent.Accent, true),
            new("▰▱▱▱▱▱", StatusAccent.Success, false),
            new("9k↑ 1k↓", StatusAccent.Dim, false),
            new("$0.99", StatusAccent.Dim, false),
        };

        // Width that only fits model + ctx + tokens: cost dies first.
        Span<StatusSeg> span = ws;
        int width = 5 + 1 + 6 + 1 + 8;
        int kept = StatusBarLayout.Fit(span, width);
        string tailAfterFirst = ws[kept - 1].Text;

        await Assert.That(kept).IsEqualTo(3);
        await Assert.That(tailAfterFirst).IsEqualTo("9k↑ 1k↓");

        // Even narrower: tokens die next.
        var ws2 = new StatusSeg[]
        {
            new("model", StatusAccent.Accent, true),
            new("▰▱▱▱▱▱", StatusAccent.Success, false),
            new("9k↑ 1k↓", StatusAccent.Dim, false),
        };
        kept = StatusBarLayout.Fit(ws2, 5 + 1 + 6);
        string second = kept >= 2 ? ws2[1].Text : "";

        await Assert.That(kept).IsEqualTo(2);
        await Assert.That(second).IsEqualTo("▰▱▱▱▱▱"); // ctx bar is the last flexible to go
    }

    [Test]
    public async Task Paint_WritesAccentsIntoCells()
    {
        var buffer = new ScreenBuffer(30, 1);
        var segs = new StatusSeg[]
        {
            new("mdl", StatusAccent.Accent, true),
            new("ok", StatusAccent.Success, false),
        };
        StatusBarWidget.Paint(buffer, new Rect(0, 0, 30, 1), segs);

        await Assert.That(buffer.Get(0, 0).Style.Fg).IsEqualTo(PackedColor.Indexed(4));
        await Assert.That((int)(buffer.Get(0, 0).Style.Attrs & StyleAttr.Bold)).IsNotEqualTo(0);
        await Assert.That(buffer.Get(5, 0).Style.Fg).IsEqualTo(PackedColor.Indexed(2));
    }

    [Test]
    public async Task ModeHints_AppendAfterModel()
    {
        var vm = Vm(StatusBarMode.AwaitingApproval);
        var ws = new StatusSeg[8];
        int n = vm.BuildSegments(ws);

        string hint = n >= 2 ? ws[1].Text : "";
        var accent = n >= 2 ? ws[1].Accent : default;
        await Assert.That(n).IsEqualTo(2);
        await Assert.That(hint).IsEqualTo("⏸ awaiting approval");
        await Assert.That(accent).IsEqualTo(StatusAccent.Warning);

        vm.Mode = StatusBarMode.Compacting;
        _ = vm.BuildSegments(ws);
        await Assert.That(ws[1].Text).IsEqualTo("compacting…");
        await Assert.That(ws[1].Accent).IsEqualTo(StatusAccent.Dim);
    }

    [Test]
    public async Task UsageFormatting_HumanBuckets()
    {
        await Assert.That(StatusViewModel.FormatCount(999)).IsEqualTo("999");
        await Assert.That(StatusViewModel.FormatCount(12_000)).IsEqualTo("12k");
        await Assert.That(StatusViewModel.FormatCount(4_500)).IsEqualTo("4.5k");
        await Assert.That(StatusViewModel.FormatCount(2_300_000)).IsEqualTo("2.3M");
    }
}
