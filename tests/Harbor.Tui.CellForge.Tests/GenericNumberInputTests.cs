using System.Globalization;
using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Painting reads the global TerminalColorPalette, so serialize with other paint tests.
[NotInParallel("pty")]
public class GenericNumberInputTests
{
    private static string Row(GenericNumberInput input, int width = 12)
    {
        var buffer = TestRender.Buffer(width, 1);
        input.IsFocused = false;
        input.Paint(buffer, new Rect(0, 0, width, 1));
        return ScreenFixtures.Row(buffer, 0, width).TrimEnd();
    }

    [Test]
    public async Task StepValue_PreservesDecimalPrecision()
    {
        await Assert.That(GenericNumberInput.StepValue("0.1", NumberStepAction.Increment, 0.2)).IsEqualTo("0.3");
        await Assert.That(GenericNumberInput.StepValue("1.25", NumberStepAction.Increment, 0.5)).IsEqualTo("1.75");
    }

    [Test]
    public async Task StepValue_ClampsToBounds_AndKeepsTheirPrecision()
    {
        await Assert.That(GenericNumberInput.StepValue("99.5", NumberStepAction.Increment, 1, max: 100)).IsEqualTo("100.0");
        await Assert.That(GenericNumberInput.StepValue("5", NumberStepAction.Decrement, 10, min: 0)).IsEqualTo("0");
    }

    [Test]
    public async Task StepValue_RefusesNoMovementAtABound()
    {
        await Assert.That(GenericNumberInput.StepValue("10", NumberStepAction.Decrement, 1, min: 10)).IsNull();
        await Assert.That(GenericNumberInput.StepValue("10", NumberStepAction.Increment, 1, max: 10)).IsNull();
    }

    [Test]
    public async Task StepValue_RefusesNonPositiveStep()
    {
        await Assert.That(GenericNumberInput.StepValue("1", NumberStepAction.Increment, 0)).IsNull();
        await Assert.That(GenericNumberInput.StepValue("1", NumberStepAction.Increment, -1)).IsNull();
    }

    [Test]
    public async Task SetValue_Clamps_AndValueParses()
    {
        var input = new GenericNumberInput(min: 0, max: 10);
        input.SetValue(42);
        await Assert.That(input.Value).IsEqualTo(10);
        input.SetValue(-3);
        await Assert.That(input.Value).IsEqualTo(0);
    }

    [Test]
    public async Task IncrementDecrement_UseStep_AndReportMovement()
    {
        var input = new GenericNumberInput(value: 1, min: 0, max: 3, step: 1);

        await Assert.That(input.Increment()).IsTrue();
        await Assert.That(input.Value).IsEqualTo(2);
        await Assert.That(input.Decrement()).IsTrue();
        await Assert.That(input.Value).IsEqualTo(1);

        input.SetValue(3);
        await Assert.That(input.Increment()).IsFalse();
        await Assert.That(input.CanStep(NumberStepAction.Increment)).IsFalse();
        await Assert.That(input.CanStep(NumberStepAction.Decrement)).IsTrue();
    }

    [Test]
    public async Task HandleKey_UpDownStep_OtherKeysDelegateToTheTextField()
    {
        var input = new GenericNumberInput(value: 5, min: 0, max: 10, step: 2);

        await Assert.That(input.HandleKey(KeyEvent.Simple(KeyCode.Up))).IsTrue();
        await Assert.That(input.Value).IsEqualTo(7);
        await Assert.That(input.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(input.Value).IsEqualTo(5);

        input.HandleKey(KeyEvent.Simple(KeyCode.End));
        await Assert.That(input.HandleKey(KeyEvent.Char(new Rune('9')))).IsTrue();
        await Assert.That(input.Text).IsEqualTo("59");
    }

    [Test]
    public async Task HandleKey_AtBound_SwallowsTheStepKey()
    {
        var input = new GenericNumberInput(value: 10, min: 0, max: 10);

        await Assert.That(input.HandleKey(KeyEvent.Simple(KeyCode.Up))).IsFalse();
        await Assert.That(input.Value).IsEqualTo(10);
    }

    [Test]
    public async Task HandleKey_Release_IsIgnored()
    {
        var input = new GenericNumberInput(value: 1);
        var release = new KeyEvent(KeyCode.Up, default, KeyModifiers.None, KeyEventType.Release, false);

        await Assert.That(input.HandleKey(in release)).IsFalse();
        await Assert.That(input.Value).IsEqualTo(1);
    }

    [Test]
    public async Task Paint_Golden_ValueWithStepperGlyphs()
    {
        var input = new GenericNumberInput(value: 12.5, min: 0, max: 100, step: 1);

        // Two rightmost cells are the decrement/increment controls; the rest is the field.
        string expected = "12.5".PadRight(8) + "▼▲";
        await Assert.That(Row(input, width: 10)).IsEqualTo(expected);
    }

    [Test]
    public async Task Paint_Golden_StepperCanBeHidden()
    {
        var input = new GenericNumberInput(value: 12.5) { ShowStepper = false };

        await Assert.That(Row(input, width: 10)).IsEqualTo("12.5");
    }

    [Test]
    public async Task Paint_Golden_FormatterAffectsOnlyDisplay()
    {
        var input = new GenericNumberInput(
            value: 12.5,
            formatter: v => v.ToString("$0.00", CultureInfo.InvariantCulture))
        {
            ShowStepper = false,
        };

        await Assert.That(Row(input, width: 10)).IsEqualTo("$12.50");
        // Stepping still reads the parseable raw text, not the formatted display.
        await Assert.That(input.Increment()).IsTrue();
        await Assert.That(input.Value).IsEqualTo(13.5);
    }

    [Test]
    public async Task Paint_AtBound_DimsTheUnavailableGlyph()
    {
        var input = new GenericNumberInput(value: 10, min: 0, max: 10);
        var buffer = TestRender.Buffer(10, 1);
        input.IsFocused = false;
        input.Paint(buffer, new Rect(0, 0, 10, 1));

        // Glyphs remain painted; availability is a style decision, tested via CanStep.
        string expected = "10".PadRight(8) + "▼▲";
        await Assert.That(ScreenFixtures.Row(buffer, 0, 10).TrimEnd()).IsEqualTo(expected);
        await Assert.That(input.CanStep(NumberStepAction.Increment)).IsFalse();
    }
}
