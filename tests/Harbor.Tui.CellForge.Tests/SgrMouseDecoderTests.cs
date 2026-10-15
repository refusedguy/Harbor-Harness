using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Parsing;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Direct unit pins for <see cref="SgrMouseDecoder"/> (#95 extract seam):
/// the decoder is a pure function of the final byte, the raw CSI parameter
/// bytes and the press context, so these vectors run without a parser
/// instance. End-to-end golden bytes stay in <c>GoldenSgrMouseTests</c>; the
/// last test pins the parser delegation wiring (press context + enqueue).
/// </summary>
public class SgrMouseDecoderTests
{
    private static byte[] Params(string parameters) => Encoding.UTF8.GetBytes(parameters);

    [Test]
    [Arguments(0, MouseButton.Left)]
    [Arguments(1, MouseButton.Middle)]
    [Arguments(2, MouseButton.Right)]
    public async Task Press_Decodes_Button_And_ZeroBased_Coords(int wireButton, MouseButton expected)
    {
        var ok = SgrMouseDecoder.TryDecode(
            (byte)'M', Params($"{wireButton};10;5"),
            MouseButton.None, false,
            out var mouse, out var nextButton, out var nextMoved);

        await Assert.That(ok).IsTrue();
        await Assert.That(mouse.Type).IsEqualTo(MouseEventType.Press);
        await Assert.That(mouse.Button).IsEqualTo(expected);
        await Assert.That(mouse.Column).IsEqualTo(9);
        await Assert.That(mouse.Row).IsEqualTo(4);
        await Assert.That(nextButton).IsEqualTo(expected);
        await Assert.That(nextMoved).IsFalse();
    }

    [Test]
    public async Task Wheel_Up_Decodes_Without_Touching_Press_Context()
    {
        var ok = SgrMouseDecoder.TryDecode(
            (byte)'M', Params("64;10;5"),
            MouseButton.Left, true,
            out var mouse, out var nextButton, out var nextMoved);

        await Assert.That(ok).IsTrue();
        await Assert.That(mouse.Type).IsEqualTo(MouseEventType.WheelUp);
        await Assert.That(mouse.Button).IsEqualTo(MouseButton.None);
        await Assert.That(nextButton).IsEqualTo(MouseButton.Left);
        await Assert.That(nextMoved).IsTrue();
    }

    [Test]
    public async Task Clean_Press_Release_Synthesizes_Click_And_Clears_Context()
    {
        var ok = SgrMouseDecoder.TryDecode(
            (byte)'m', Params("0;10;5"),
            MouseButton.Left, false,
            out var mouse, out var nextButton, out var nextMoved);

        await Assert.That(ok).IsTrue();
        await Assert.That(mouse.Type).IsEqualTo(MouseEventType.Click);
        await Assert.That(mouse.Button).IsEqualTo(MouseButton.Left);
        await Assert.That(nextButton).IsEqualTo(MouseButton.None);
        await Assert.That(nextMoved).IsFalse();
    }

    [Test]
    public async Task Motion_After_Press_Synthesizes_Drag()
    {
        var ok = SgrMouseDecoder.TryDecode(
            (byte)'M', Params("32;10;5"),
            MouseButton.Left, false,
            out var mouse, out var nextButton, out var nextMoved);

        await Assert.That(ok).IsTrue();
        await Assert.That(mouse.Type).IsEqualTo(MouseEventType.Drag);
        await Assert.That(nextButton).IsEqualTo(MouseButton.Left);
        await Assert.That(nextMoved).IsTrue();
    }

    [Test]
    public async Task Unpaired_Release_Is_Not_Decodable()
    {
        var ok = SgrMouseDecoder.TryDecode(
            (byte)'m', Params("0;10;5"),
            MouseButton.None, false,
            out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Missing_Params_Are_Not_Decodable()
    {
        var ok = SgrMouseDecoder.TryDecode(
            (byte)'M', Params(string.Empty),
            MouseButton.None, false,
            out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Parser_Delegation_Preserves_Sgr_Wiring()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B[<0;10;5M");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(InputEventKind.Mouse);
        await Assert.That(events[0].Mouse.Type).IsEqualTo(MouseEventType.Press);
        await Assert.That(events[0].Mouse.Button).IsEqualTo(MouseButton.Left);
    }
}
