using System.Text;
using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Painting reads the global TerminalColorPalette, so serialize with other paint tests.
[NotInParallel("pty")]
public class GenericStepperTests
{
    private static string Paint(GenericStepperState state, int width = 9)
    {
        var buffer = TestRender.Buffer(width, 1);
        GenericStepper.Paint(buffer, new Rect(0, 0, width, 1), state);
        return ScreenFixtures.Row(buffer, 0, width).TrimEnd();
    }

    [Test]
    public async Task StatusAt_SplitsCompletedCurrentUpcoming()
    {
        var state = new GenericStepperState(Current: 1, Count: 3);

        await Assert.That(GenericStepper.StatusAt(0, state)).IsEqualTo(StepStatus.Completed);
        await Assert.That(GenericStepper.StatusAt(1, state)).IsEqualTo(StepStatus.Current);
        await Assert.That(GenericStepper.StatusAt(2, state)).IsEqualTo(StepStatus.Upcoming);
        await Assert.That(GenericStepper.StatusAt(3, state)).IsEqualTo(StepStatus.Upcoming);
    }

    [Test]
    public async Task StatusAt_EmptyStepper_HasNoCurrent()
    {
        await Assert.That(GenericStepper.StatusAt(0, new GenericStepperState(0, 0))).IsEqualTo(StepStatus.Upcoming);
    }

    [Test]
    public async Task GlyphFor_MapsEachStatus()
    {
        await Assert.That(GenericStepper.GlyphFor(StepStatus.Completed)).IsEqualTo(GenericStepper.CompletedGlyph);
        await Assert.That(GenericStepper.GlyphFor(StepStatus.Current)).IsEqualTo(GenericStepper.CurrentGlyph);
        await Assert.That(GenericStepper.GlyphFor(StepStatus.Upcoming)).IsEqualTo(GenericStepper.UpcomingGlyph);
    }

    [Test]
    public async Task Move_AndSelect_ClampToTheStepRange()
    {
        var state = new GenericStepperState(Current: 1, Count: 3);

        await Assert.That(state.Move(-1).Current).IsEqualTo(0);
        await Assert.That(state.Move(-1).Move(-1).Current).IsEqualTo(0);
        await Assert.That(state.Move(1).Current).IsEqualTo(2);
        await Assert.That(state.Move(1).Move(1).Current).IsEqualTo(2);
        await Assert.That(state.Select(99).Current).IsEqualTo(2);
        await Assert.That(state.Select(-5).Current).IsEqualTo(0);
    }

    [Test]
    public async Task Move_FromOutOfRange_StartsAtClampedCurrent()
    {
        var state = new GenericStepperState(Current: 99, Count: 3);

        await Assert.That(state.ClampedCurrent).IsEqualTo(2);
        await Assert.That(state.Move(-1).Current).IsEqualTo(1);
    }

    [Test]
    public async Task Paint_Golden_ThreeStepsMiddleSelected()
    {
        await Assert.That(Paint(new GenericStepperState(1, 3), width: 7)).IsEqualTo("●─◉─○");
    }

    [Test]
    public async Task Paint_Golden_FirstAndLastSelected()
    {
        await Assert.That(Paint(new GenericStepperState(0, 3), width: 7)).IsEqualTo("◉─○─○");
        await Assert.That(Paint(new GenericStepperState(2, 3), width: 7)).IsEqualTo("●─●─◉");
    }

    [Test]
    public async Task Paint_TruncatesWhenWidthIsTooSmall()
    {
        await Assert.That(Paint(new GenericStepperState(1, 3), width: 3)).IsEqualTo("●─◉");
    }

    [Test]
    public async Task Paint_EmptyStepper_DrawsNothing()
    {
        await Assert.That(Paint(new GenericStepperState(0, 0), width: 7)).IsEmpty();
    }

    [Test]
    public async Task Paint_ZeroWidthOrHeight_IsANoOp()
    {
        var buffer = TestRender.Buffer(8, 2);
        GenericStepper.Paint(buffer, new Rect(0, 0, 0, 2), new GenericStepperState(0, 3));
        GenericStepper.Paint(buffer, new Rect(0, 0, 8, 0), new GenericStepperState(0, 3));

        var sb = new StringBuilder();
        for (int y = 0; y < buffer.Rows; y++)
        {
            sb.Append(ScreenFixtures.Row(buffer, y, buffer.Cols).TrimEnd()).Append('\n');
        }

        await Assert.That(sb.ToString().Trim()).IsEmpty();
    }
}
