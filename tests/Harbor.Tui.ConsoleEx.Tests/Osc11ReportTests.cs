using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Parsing;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>OSC 11 background-report interception (auto-theme §P3.3): the
/// parser captures OSC string bodies and surfaces «11;rgb:…» answers as
/// capability events; every other OSC string stays discarded.</summary>
public class Osc11ReportTests
{
    private readonly EscapeSequenceParser _parser = new();

    [Test]
    public async Task Osc11_BelTerminated_Rgb8Bit_EmitsCapability()
    {
        var events = T.Feed(_parser, "\u001B]11;rgb:0a/0e/14\u0007");
        await Assert.That(events.Length).IsEqualTo(1);
        var evt = events[0];
        await Assert.That(evt.Kind).IsEqualTo(InputEventKind.Capability);
        await Assert.That(evt.Capability.Kind).IsEqualTo(CapabilityEventKind.Osc11BackgroundReport);
        await Assert.That(evt.Capability.Red).IsEqualTo(0x0A);
        await Assert.That(evt.Capability.Green).IsEqualTo(0x0E);
        await Assert.That(evt.Capability.Blue).IsEqualTo(0x14);
        await Assert.That(_parser.State).IsEqualTo(ParserState.Ground);
    }

    [Test]
    public async Task Osc11_StTerminated_Rgb16Bit_KeepsHighByte()
    {
        var events = T.Feed(_parser, "\u001B]11;rgb:ffff/cccc/0000\u001B\\");
        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Capability.Kind).IsEqualTo(CapabilityEventKind.Osc11BackgroundReport);
        await Assert.That(events[0].Capability.Red).IsEqualTo(0xFF);
        await Assert.That(events[0].Capability.Green).IsEqualTo(0xCC);
        await Assert.That(events[0].Capability.Blue).IsEqualTo(0x00);
    }

    [Test]
    public async Task Osc11_Split_Across_Chunks_EmitsOnce()
    {
        var events = T.Feed(_parser, "\u001B]11;rg", "b:0a/0e/", "14\u0007");
        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Capability.Blue).IsEqualTo(0x14);
    }

    [Test]
    public async Task OtherOsc_String_Title_StaysDiscarded()
    {
        var events = T.Feed(_parser, "\u001B]0;window title\u0007");
        await Assert.That(events.Length).IsEqualTo(0);
        await Assert.That(_parser.State).IsEqualTo(ParserState.Ground);
    }

    [Test]
    public async Task Osc11_Malformed_Payload_EmitsNothing()
    {
        var events = T.Feed(_parser, "\u001B]11;nonsense\u0007");
        await Assert.That(events.Length).IsEqualTo(0);
        await Assert.That(_parser.State).IsEqualTo(ParserState.Ground);
    }

    [Test]
    public async Task Reset_Between_Reports_DoesNotCarryOver()
    {
        _ = T.Feed(_parser, "\u001B]11;rgb:01/02/03"); // unterminated — capture holds
        _parser.Reset();
        var events = T.Feed(_parser, "\u001B]11;rgb:aa/bb/cc\u0007");
        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Capability.Red).IsEqualTo(0xAA);
    }
}
