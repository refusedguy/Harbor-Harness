using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>OSC 1337 inline-image encoder (iTerm2 protocol family) —
/// byte-golden vectors: envelope shape, name sanitization, payload caps.</summary>
public class Osc1337ImageTests
{
    private static byte[] FakePng(int size = 64)
    {
        var bytes = new byte[size];
        bytes[0] = 0x89;
        bytes[1] = (byte)'P';
        return bytes;
    }

    [Test]
    public async Task Encode_ProducesIterm2Envelope_WithBelTerminator()
    {
        byte[] data = [0x01, 0x02, 0x03];
        string seq = Encoding.UTF8.GetString(Osc1337Image.Encode("shot.png", data)!);

        string expected =
            "\u001B]1337;File=name=shot.png;size=3;inline=1;preserveAspectRatio=1:"
            + Convert.ToBase64String(data)
            + "\u0007";
        await Assert.That(seq).IsEqualTo(expected);
    }

    [Test]
    public async Task Encode_NameSanitized_EnvelopeCannotBeBroken()
    {
        string seq = Encoding.UTF8.GetString(
            Osc1337Image.Encode("evil;x\u001B]52;c;\u0007.png", [0x01])!);

        // The name segment holds no ';' — extra File keys cannot be injected,
        // and ESC/BEL inside the name are neutralized (']' stays, it is legal).
        await Assert.That(seq.StartsWith("\u001B]1337;File=name=evil_x_]52_c__.png;size=1;")).IsTrue();
        int headerEnd = seq.IndexOf(':');
        await Assert.That(seq[7..headerEnd]).DoesNotContain("\u001B");
    }

    [Test]
    public async Task Encode_EmptyPayload_ReturnsNull_TextCardStays()
    {
        await Assert.That(Osc1337Image.Encode("a.png", ReadOnlySpan<byte>.Empty)).IsNull();
    }

    [Test]
    public async Task Encode_OversizePayload_ReturnsNull()
    {
        var data = new byte[Osc1337Image.MaxDataBytes + 1];
        await Assert.That(Osc1337Image.Encode("big.png", data)).IsNull();
    }

    [Test]
    public async Task Encode_EmptyName_ReturnsNull()
    {
        await Assert.That(Osc1337Image.Encode("", [0x01])).IsNull();
    }

    [Test]
    public async Task Encode_JpegRidesSameEnvelope_TerminalSniffsFormat()
    {
        byte[] data = [0xFF, 0xD8, 0xFF, 0xE0];
        string seq = Encoding.UTF8.GetString(Osc1337Image.Encode("photo.jpg", data)!);

        await Assert.That(seq.StartsWith("\u001B]1337;File=name=photo.jpg;size=4;")).IsTrue();
        await Assert.That(seq).Contains("inline=1");
    }
}
