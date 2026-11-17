using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Pure state/keyboard contract: no palette, no buffer, nothing to serialize.
public class SliderTests
{
    [Test]
    public async Task Clamp_ClampsAndSnapsToStepGrid()
    {
        var state = new SliderState(0, Min: 0, Max: 100, Step: 10);
        await Assert.That(Slider.Clamp(state, 27)).IsEqualTo(30);
        await Assert.That(Slider.Clamp(state, 24)).IsEqualTo(20);
        await Assert.That(Slider.Clamp(state, -5)).IsEqualTo(0);
        await Assert.That(Slider.Clamp(state, 105)).IsEqualTo(100);
        await Assert.That(Slider.Clamp(state, double.NaN)).IsEqualTo(0);
    }

    [Test]
    public async Task Clamp_MaxWinsWhenNearestGridPointOvershoots()
    {
        // Grid is 5,15,25,... A value of 20 would snap to 25, past Max=22.
        var state = new SliderState(0, Min: 5, Max: 22, Step: 10);
        await Assert.That(Slider.Clamp(state, 20)).IsEqualTo(22);
    }

    [Test]
    public async Task Clamp_NonPositiveOrNonFiniteStep_SkipsSnapping()
    {
        await Assert.That(Slider.Clamp(new SliderState(0, 0, 100, 0), 27)).IsEqualTo(27);
        await Assert.That(Slider.Clamp(new SliderState(0, 0, 100, -1), 27)).IsEqualTo(27);
        await Assert.That(Slider.Clamp(new SliderState(0, 0, 100, double.NaN), 27)).IsEqualTo(27);
    }

    [Test]
    public async Task Normalize_OffGridExternalValue_SnapsForDisplay()
    {
        var state = new SliderState(27, Min: 0, Max: 100, Step: 10);
        await Assert.That(Slider.Normalize(state)).IsEqualTo(30);
        await Assert.That(Slider.Normalize(state with { Value = double.NaN })).IsEqualTo(0);
    }

    [Test]
    public async Task Ratio_MapsValueToBoundsAndClamps()
    {
        var state = new SliderState(50, Min: 0, Max: 200, Step: 10);
        await Assert.That(Slider.Ratio(state)).IsEqualTo(0.25);
        await Assert.That(Slider.Ratio(state with { Value = 300 })).IsEqualTo(1);
        await Assert.That(Slider.Ratio(state with { Value = -10 })).IsEqualTo(0);
        await Assert.That(Slider.Ratio(new SliderState(5, Min: 5, Max: 5, Step: 1))).IsEqualTo(0);
    }

    [Test]
    public async Task Ratio_FractionalStep_StaysInRange()
    {
        var state = new SliderState(0.3, Min: 0, Max: 1, Step: 0.1);
        await Assert.That(Slider.Ratio(state)).IsEqualTo(0.3).Within(0.0001);
    }

    [Test]
    public async Task StepUpDown_MoveByOneStepAndStopAtBounds()
    {
        var state = new SliderState(10, Min: 0, Max: 25, Step: 10);
        await Assert.That(Slider.StepUp(state)).IsEqualTo(20);
        await Assert.That(Slider.StepDown(state)).IsEqualTo(0);

        var nearMax = state with { Value = 20 };
        await Assert.That(Slider.StepUp(nearMax)).IsEqualTo(25);
        await Assert.That(Slider.StepDown(nearMax)).IsEqualTo(10);

        var noStep = state with { Step = 0 };
        await Assert.That(Slider.StepUp(noStep)).IsEqualTo(10);
        await Assert.That(Slider.StepDown(noStep)).IsEqualTo(10);
    }

    [Test]
    public async Task TryHandleKey_ArrowsAndHomeEnd_FoldToExpectedValues()
    {
        var state = new SliderState(50, Min: 0, Max: 100, Step: 10);
        (UiKey Key, double Expected)[] cases =
        [
            (new UiKey(UiKeyCode.Right), 60),
            (new UiKey(UiKeyCode.Up), 60),
            (new UiKey(UiKeyCode.Left), 40),
            (new UiKey(UiKeyCode.Down), 40),
            (new UiKey(UiKeyCode.Home), 0),
            (new UiKey(UiKeyCode.End), 100),
        ];

        foreach (var (key, expected) in cases)
        {
            bool handled = Slider.TryHandleKey(key, state, out var next);
            await Assert.That(handled).IsTrue();
            await Assert.That(next.Value).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task TryHandleKey_OtherKeys_AreNotConsumed()
    {
        var state = new SliderState(50, Min: 0, Max: 100, Step: 10);
        UiKey[] passThrough =
        [
            UiKey.ForChar('a'),
            new UiKey(UiKeyCode.Enter),
            new UiKey(UiKeyCode.Escape),
            new UiKey(UiKeyCode.Tab),
            new UiKey(UiKeyCode.PageUp),
            UiKey.Unknown,
        ];

        foreach (var key in passThrough)
        {
            bool handled = Slider.TryHandleKey(key, state, out var next);
            await Assert.That(handled).IsFalse();
            await Assert.That(next).IsEqualTo(default(SliderState));
        }
    }

    [Test]
    public async Task TryHandleKey_HostCommandChord_IsNotConsumed()
    {
        var state = new SliderState(50, Min: 0, Max: 100, Step: 10);
        bool handled = Slider.TryHandleKey(
            new UiKey(UiKeyCode.Right, KeyModifierSet.Ctrl), state, out var next);
        await Assert.That(handled).IsFalse();
        await Assert.That(next).IsEqualTo(default(SliderState));
    }

    [Test]
    public async Task TryHandleKey_PreservesOwnerSuppliedPolicy()
    {
        Func<double, string> formatter = v => $"[{v:0}]";
        var state = new SliderState(5, Min: 5, Max: 25, Step: 10, Formatter: formatter);
        bool handled = Slider.TryHandleKey(new UiKey(UiKeyCode.Right), state, out var next);
        await Assert.That(handled).IsTrue();
        await Assert.That(next.Value).IsEqualTo(15);
        await Assert.That(next.Min).IsEqualTo(5);
        await Assert.That(next.Max).IsEqualTo(25);
        await Assert.That(next.Step).IsEqualTo(10);
        await Assert.That(next.Formatter).IsSameReferenceAs(formatter);
    }

    [Test]
    public async Task Format_UsesFormatterOrInvariantDefault()
    {
        var custom = new SliderState(27, Min: 0, Max: 100, Step: 10, Formatter: v => $"v={v:0}");
        await Assert.That(Slider.Format(custom)).IsEqualTo("v=30");
        await Assert.That(Slider.Format(new SliderState(0.5, Step: 0.25))).IsEqualTo("0.5");
        await Assert.That(Slider.Format(new SliderState(0))).IsEqualTo("0");
    }
}

// Rendering reads global TerminalColorPalette, so serialize vs theme tests.
[NotInParallel("pty")]
public class SliderPaintTests
{
    private static SliderState Bare(double value, double step = 10) =>
        new(value, Min: 0, Max: 100, Step: step, Formatter: _ => "");

    private static string Art(int width, SliderState state)
    {
        var buffer = TestRender.Buffer(width, 1);
        Slider.Paint(buffer, new Rect(0, 0, width, 1), state);
        return TestRender.Art(buffer).TrimEnd('\n');
    }

    [Test]
    public async Task Paint_BareTrack_Golden()
    {
        await Assert.That(Art(10, Bare(0))).IsEqualTo(new string(GaugeBar.Track, 10));
        await Assert.That(Art(10, Bare(50)))
            .IsEqualTo(new string(GaugeBar.Filled, 5) + new string(GaugeBar.Track, 5));
        await Assert.That(Art(10, Bare(100))).IsEqualTo(new string(GaugeBar.Filled, 10));
    }

    [Test]
    public async Task Paint_OffGridValue_SnapsToStepInGolden()
    {
        // 27 snaps to 30 on the step-10 grid → 3 of 10 cells filled.
        await Assert.That(Art(10, Bare(27)))
            .IsEqualTo(new string(GaugeBar.Filled, 3) + new string(GaugeBar.Track, 7));
    }

    [Test]
    public async Task Paint_FormattedResult_OverlayedOnTrack()
    {
        var state = new SliderState(50, Min: 0, Max: 100, Step: 10, Formatter: v => $"{v:0}");
        string art = Art(10, state);
        await Assert.That(art.Contains("50")).IsTrue();
        await Assert.That(art.Length).IsEqualTo(10);
    }

    [Test]
    public async Task Paint_DelegatesToGaugeBarTrackAndLabel()
    {
        var state = new SliderState(42, Min: 0, Max: 100, Step: 1, Formatter: v => $"{v:0}%");
        var sliderBuffer = TestRender.Buffer(9, 1);
        var gaugeBuffer = TestRender.Buffer(9, 1);
        Slider.Paint(sliderBuffer, new Rect(0, 0, 9, 1), state);
        GaugeBar.Paint(gaugeBuffer, new Rect(0, 0, 9, 1),
            new GaugeState(Slider.Ratio(state), Slider.Format(state)));
        await Assert.That(TestRender.Art(sliderBuffer)).IsEqualTo(TestRender.Art(gaugeBuffer));
    }

    [Test]
    public async Task Paint_DegenerateRect_NoThrow()
    {
        var buffer = TestRender.Buffer(4, 1);
        Slider.Paint(buffer, new Rect(0, 0, 0, 1), Bare(50));
        Slider.Paint(buffer, new Rect(0, 0, 4, 0), Bare(50));
        await Assert.That(TestRender.Art(buffer).Length).IsGreaterThan(0);
    }
}
