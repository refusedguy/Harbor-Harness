using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Parsing;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Direct unit pins for <see cref="KittyKeyDecoder"/> (#95 extract seam):
/// the decoder is a pure function of the raw CSI parameter bytes, so these
/// vectors run without a parser instance. End-to-end golden bytes stay in
/// <c>GoldenKittyKeyTests</c>; the last test pins the parser delegation wiring.
/// </summary>
public class KittyKeyDecoderTests
{
    private static byte[] Params(string parameters) => Encoding.UTF8.GetBytes(parameters);

    [Test]
    [Arguments("13", EngineInput.KeyCode.Enter)]
    [Arguments("27", EngineInput.KeyCode.Escape)]
    [Arguments("9", EngineInput.KeyCode.Tab)]
    [Arguments("127", EngineInput.KeyCode.Backspace)]
    public async Task Functional_Keys_Decode(string parameters, EngineInput.KeyCode expected)
    {
        var ok = KittyKeyDecoder.TryDecode(Params(parameters), out var key);

        await Assert.That(ok).IsTrue();
        await Assert.That(key.Key).IsEqualTo(expected);
        await Assert.That(key.EventType).IsEqualTo(EngineInput.KeyEventType.Press);
        await Assert.That(key.IsKittyEncoded).IsTrue();
    }

    [Test]
    public async Task Modified_Char_Decodes_Mods_And_Char()
    {
        var ok = KittyKeyDecoder.TryDecode(Params("97;5"), out var key);

        await Assert.That(ok).IsTrue();
        await Assert.That(key.Key).IsEqualTo(EngineInput.KeyCode.Char);
        await Assert.That(key.Character.ToString()).IsEqualTo("a");
        await Assert.That(key.Modifiers).IsEqualTo(EngineInput.KeyModifiers.Alt);
    }

    [Test]
    public async Task Release_Event_Type_Decodes()
    {
        var ok = KittyKeyDecoder.TryDecode(Params("13;1:3"), out var key);

        await Assert.That(ok).IsTrue();
        await Assert.That(key.Key).IsEqualTo(EngineInput.KeyCode.Enter);
        await Assert.That(key.EventType).IsEqualTo(EngineInput.KeyEventType.Release);
        await Assert.That(key.Modifiers).IsEqualTo(EngineInput.KeyModifiers.None);
    }

    [Test]
    public async Task Missing_Primary_Is_Not_Decodable()
    {
        var ok = KittyKeyDecoder.TryDecode(Params(string.Empty), out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Parser_Delegation_Preserves_Kitty_Wiring()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B[13u");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], EngineInput.KeyCode.Enter, kitty: true);
    }
}
