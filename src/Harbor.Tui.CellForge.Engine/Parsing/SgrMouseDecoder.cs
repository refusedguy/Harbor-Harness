using Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Parsing;

/// <summary>
/// SGR mouse decoder (design §3.2), extracted from
/// <see cref="EscapeSequenceParser"/> (#95, one extract per PR):
/// CSI &lt; button ; column ; row M|m.
/// Button bits: id=bits0-1, shift=4, alt(meta)=8, ctrl=16, motion=32,
/// wheel=64. Coordinates are one-based on the wire, stored zero-based.
/// Click = clean press→release without motion; Drag = motion with a held
/// button; Release = release after drag (§3.2/§3.3).
/// Pure and allocation-free: press context is threaded through parameters
/// (the parser owns <c>_mousePressedButton</c>/<c>_mouseMovedSincePress</c>
/// and applies the returned next state only on success — every failure path
/// in the source left that state untouched). The only shared dependency is
/// the CSI scalar scanner (<see cref="EscapeSequenceParser.IntParamAt"/>).
/// Returns false when the sequence carries no event (caller counts it ignored).
/// </summary>
public static class SgrMouseDecoder
{
    public static bool TryDecode(
        byte finalByte,
        ReadOnlySpan<byte> parameters,
        MouseButton pressedButton,
        bool movedSincePress,
        out MouseEvent mouseEvent,
        out MouseButton nextPressedButton,
        out bool nextMovedSincePress)
    {
        var buttonRaw = EscapeSequenceParser.IntParamAt(parameters, 0);
        var column = EscapeSequenceParser.IntParamAt(parameters, 1);
        var row = EscapeSequenceParser.IntParamAt(parameters, 2);
        if (buttonRaw < 0 || column < 0 || row < 0)
        {
            mouseEvent = default;
            nextPressedButton = pressedButton;
            nextMovedSincePress = movedSincePress;
            return false;
        }

        var mods = KeyModifiers.None;
        if ((buttonRaw & 0x04) != 0)
        {
            mods |= KeyModifiers.Shift;
        }
        if ((buttonRaw & 0x08) != 0)
        {
            mods |= KeyModifiers.Alt;
        }
        if ((buttonRaw & 0x10) != 0)
        {
            mods |= KeyModifiers.Ctrl;
        }

        // Zero-based viewport coordinates; values may exceed the window
        // (release-after-drag) — consumers clamp before indexing.
        var col = column - 1;
        var r = row - 1;

        var buttonId = buttonRaw & 0x03;

        if ((buttonRaw & 0x40) != 0)
        {
            // Wheel: scroll-id in bits 0-1; 64=up, 65=down; 66+ horizontal —
            // ignored by design (§3.2).
            if (buttonId > 1)
            {
                mouseEvent = default;
                nextPressedButton = pressedButton;
                nextMovedSincePress = movedSincePress;
                return false;
            }

            var wheel = buttonId == 0 ? MouseEventType.WheelUp : MouseEventType.WheelDown;
            mouseEvent = new MouseEvent(wheel, MouseButton.None, col, r, mods);
            nextPressedButton = pressedButton;
            nextMovedSincePress = movedSincePress;
            return true;
        }

        if (finalByte == (byte)'m')
        {
            // Release: unpaired releases (no press context) are dropped.
            if (pressedButton == MouseButton.None || buttonId > 2)
            {
                mouseEvent = default;
                nextPressedButton = pressedButton;
                nextMovedSincePress = movedSincePress;
                return false;
            }

            var releasedButton = pressedButton;
            var wasCleanClick = !movedSincePress;
            nextPressedButton = MouseButton.None;
            nextMovedSincePress = false;

            mouseEvent = new MouseEvent(
                wasCleanClick ? MouseEventType.Click : MouseEventType.Release,
                releasedButton, col, r, mods);
            return true;
        }

        // Final 'M': press or motion(drag).
        if ((buttonRaw & 0x20) != 0)
        {
            // Motion: only meaningful while a tracked button is held (mode 1002).
            if (pressedButton == MouseButton.None || buttonId > 2)
            {
                mouseEvent = default;
                nextPressedButton = pressedButton;
                nextMovedSincePress = movedSincePress;
                return false;
            }

            nextPressedButton = pressedButton;
            nextMovedSincePress = true;
            mouseEvent = new MouseEvent(MouseEventType.Drag, pressedButton, col, r, mods);
            return true;
        }

        if (buttonId > 2)
        {
            // Legacy "release without button" (id 3) and reserved ids.
            mouseEvent = default;
            nextPressedButton = pressedButton;
            nextMovedSincePress = movedSincePress;
            return false;
        }

        nextPressedButton = (MouseButton)(buttonId + 1);
        nextMovedSincePress = false;
        mouseEvent = new MouseEvent(MouseEventType.Press, nextPressedButton, col, r, mods);
        return true;
    }
}
