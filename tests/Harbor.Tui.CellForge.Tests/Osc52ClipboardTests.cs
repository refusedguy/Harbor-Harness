using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

public class Osc52ClipboardTests
{
    [Test]
    public async Task Encode_Ascii_Base64Payload()
    {
        string seq = Osc52Clipboard.Encode("hello");
        await Assert.That(seq).IsEqualTo("\u001B]52;c;aGVsbG8=\u0007");
    }

    [Test]
    public async Task Encode_Utf8Text_Base64DecodesBack()
    {
        string text = "привет ✓";
        string seq = Osc52Clipboard.Encode(text);

        int start = seq.IndexOf(';') + 1;
        int end = seq.LastIndexOf(';') + 1;
        string payload = seq[end..^1];

        byte[] bytes = Convert.FromBase64String(payload);
        await Assert.That(Encoding.UTF8.GetString(bytes)).IsEqualTo(text);
    }

    [Test]
    public async Task Encode_EmptyText_ClearSequence()
    {
        await Assert.That(Osc52Clipboard.Encode("")).IsEqualTo("\u001B]52;c;\u0007");
        await Assert.That(Osc52Clipboard.ClearSequence).IsEqualTo("\u001B]52;c;\u0007");
    }

    [Test]
    public async Task EncodeSelection_UsesSelector()
    {
        string seq = Osc52Clipboard.EncodeSelection("p", "hi");
        await Assert.That(seq).StartsWith("\u001B]52;p;");
    }

    [Test]
    public async Task EncodeSelection_EscInSelector_Throws()
    {
        await Assert.That(() => Osc52Clipboard.EncodeSelection("c\u001B", "x"))
            .Throws<ArgumentException>();
        await Assert.That(() => Osc52Clipboard.EncodeSelection("c;x", "x"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Encode_HugeText_TruncatedToCap()
    {
        string text = new string('a', 200_000);
        string seq = Osc52Clipboard.Encode(text);

        int payloadStart = seq.LastIndexOf(';') + 1;
        int payloadLen = seq.Length - payloadStart - 1;
        await Assert.That(payloadLen).IsLessThanOrEqualTo(Osc52Clipboard.MaxPayloadChars);

        byte[] bytes = Convert.FromBase64String(seq[payloadStart..^1]);
        await Assert.That(Encoding.UTF8.GetString(bytes).Length)
            .IsLessThanOrEqualTo(text.Length);
    }
}
