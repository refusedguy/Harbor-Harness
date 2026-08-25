using System.Text;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Parsing;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Golden-byte vectors for SGR mouse (design §3.1–§3.3): modes 1000/1002 with
/// 1006 encoding, zero-based coordinate conversion, modifier bits, Click vs
/// Drag vs Release synthesis, wheel mapping and the >223-column case that
/// legacy X10 encoding cannot represent.
/// </summary>
public class GoldenSgrMouseTests
{
    private readonly EscapeSequenceParser _parser = new();

    private static async Task AssertMouse(
        InputEvent evt,
        MouseEventType type,
        MouseButton button,
        int col,
        int row,
        KeyModifiers mods = KeyModifiers.None)
    {
        await Assert.That(evt.Kind).IsEqualTo(InputEventKind.Mouse);
        await Assert.That(evt.Mouse.Type).IsEqualTo(type);
        await Assert.That(evt.Mouse.Button).IsEqualTo(button);
        await Assert.That(evt.Mouse.Column).IsEqualTo(col);
        await Assert.That(evt.Mouse.Row).IsEqualTo(row);
        await Assert.That(evt.Mouse.Modifiers).IsEqualTo(mods);
    }

    [Test]
    [Arguments(0, MouseButton.Left)]
    [Arguments(1, MouseButton.Middle)]
    [Arguments(2, MouseButton.Right)]
    public async Task Press_Decodes_Button_And_ZeroBased_Coords(int wireButton, MouseButton expected)
    {
        var events = T.Feed(_parser, $"\u001B[<{wireButton};10;5M");

        await Assert.That(events.Length).IsEqualTo(1);
        await AssertMouse(events[0], MouseEventType.Press, expected, 9, 4);
    }

    [Test]
    public async Task Clean_Press_Release_Synthesizes_Click()
    {
        var first = T.Feed(_parser, "\u001B[<0;10;5M");
        var second = T.Feed(_parser, "\u001B[<0;10;5m");

        await AssertMouse(first[0], MouseEventType.Press, MouseButton.Left, 9, 4);
        await AssertMouse(second[0], MouseEventType.Click, MouseButton.Left, 9, 4);
    }

    [Test]
    public async Task Motion_With_Held_Button_Yields_Drag_Then_Release()
    {
        var press = T.Feed(_parser, "\u001B[<0;10;5M");
        var drag = T.Feed(_parser, "\u001B[<32;12;7M");
        var release = T.Feed(_parser, "\u001B[<0;12;7m");

        await AssertMouse(press[0], MouseEventType.Press, MouseButton.Left, 9, 4);
        // Motion bit 32 keeps the held button id in the low bits.
        await AssertMouse(drag[0], MouseEventType.Drag, MouseButton.Left, 11, 6);
        await AssertMouse(release[0], MouseEventType.Release, MouseButton.Left, 11, 6);
    }

    [Test]
    public async Task Drag_With_Right_Button_Tracks_Correct_Button()
    {
        _ = T.Feed(_parser, "\u001B[<2;3;3M");
        var drag = T.Feed(_parser, "\u001B[<34;8;6M");

        await AssertMouse(drag[0], MouseEventType.Drag, MouseButton.Right, 7, 5);
    }

    [Test]
    public async Task Release_At_Coordinate_Outside_Window_Is_Passed_Unclamped()
    {
        // §3.3: release after drag can land beyond the viewport; SGR carries
        // it honestly — consumers clamp before indexing.
        var press = T.Feed(_parser, "\u001B[<0;10;5M");
        _ = T.Feed(_parser, "\u001B[<32;300;120M");
        var release = T.Feed(_parser, "\u001B[<0;300;120m");

        await AssertMouse(release[0], MouseEventType.Release, MouseButton.Left, 299, 119);
        await Assert.That(press.Length).IsEqualTo(1);
    }

    [Test]
    [Arguments("\u001B[<64;5;5M", MouseEventType.WheelUp)]
    [Arguments("\u001B[<65;5;5M", MouseEventType.WheelDown)]
    public async Task Wheel_Ticks_Map_To_Scroll(string input, MouseEventType expected)
    {
        var events = T.Feed(_parser, input);

        await Assert.That(events.Length).IsEqualTo(1);
        await AssertMouse(events[0], expected, MouseButton.None, 4, 4);
    }

    [Test]
    [Arguments("\u001B[<66;5;5M")] // horizontal wheel left
    [Arguments("\u001B[<67;5;5M")] // horizontal wheel right
    public async Task Horizontal_Wheel_Is_Ignored_By_Design(string input)
    {
        var events = T.Feed(_parser, input);

        await Assert.That(events.Length).IsEqualTo(0);
        await Assert.That(_parser.IgnoredSequenceCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    [Arguments("\u001B[<4;3;3M", KeyModifiers.Shift)]
    [Arguments("\u001B[<8;3;3M", KeyModifiers.Alt)]
    [Arguments("\u001B[<16;3;3M", KeyModifiers.Ctrl)]
    [Arguments("\u001B[<28;3;3M", KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Ctrl)]
    public async Task Modifier_Bits_Ride_On_Mouse_Events(string input, KeyModifiers expectedMods)
    {
        var events = T.Feed(_parser, input);

        await Assert.That(events.Length).IsEqualTo(1);
        await AssertMouse(events[0], MouseEventType.Press, MouseButton.Left, 2, 2, expectedMods);
    }

    [Test]
    public async Task Modified_Wheel_Carries_Modifiers()
    {
        // Ctrl+WheelUp: 64 + ctrl(16) = 80.
        var events = T.Feed(_parser, "\u001B[<80;5;5M");

        await Assert.That(events.Length).IsEqualTo(1);
        await AssertMouse(events[0], MouseEventType.WheelUp, MouseButton.None, 4, 4, KeyModifiers.Ctrl);
    }

    [Test]
    public async Task Unpaired_Release_Is_Dropped()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B[<0;10;5m");

        await Assert.That(events.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Motion_Without_Press_Context_Is_Dropped()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B[<32;12;7M");

        await Assert.That(events.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Malformed_Mouse_With_Missing_Coords_Is_Dropped()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B[<0;10M");

        await Assert.That(events.Length).IsEqualTo(0);
        await Assert.That(new EscapeSequenceParser().MalformedSequenceCount).IsEqualTo(0);
    }

    [Test]
    public async Task Mouse_And_Key_Events_Interleave_In_One_Chunk()
    {
        // §3.3 #6: mouse and keyboard bytes arrive interleaved — pure byte
        // state machine must not lose either.
        var events = T.Feed(_parser, "\u001B[<64;1;1Ma\u001B[A\u001B[<64;1;1M");

        await Assert.That(events.Length).IsEqualTo(4);
        await AssertMouse(events[0], MouseEventType.WheelUp, MouseButton.None, 0, 0);
        await A.IsChar(events[1], new Rune('a'));
        await A.IsKey(events[2], KeyCode.Up);
        await AssertMouse(events[3], MouseEventType.WheelUp, MouseButton.None, 0, 0);
    }
}
