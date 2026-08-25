using Harbor.Tui.Ansi;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.Tests;

public class TerminalQrRendererTests
{
    [Test]
    public async Task Render_ReturnsNonEmptyString_ForValidUri()
    {
        string result = TerminalQrRenderer.Render(new Uri("https://example.com"));
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Render_ContainsHalfBlockCharacters_ForValidUri()
    {
        string result = TerminalQrRenderer.Render(new Uri("https://example.com"));
        await Assert.That(result).Contains("█");
        await Assert.That(result).Contains("▀");
        await Assert.That(result).Contains("▄");
    }

    [Test]
    public async Task Render_ProducesDifferentOutput_ForDifferentUris()
    {
        string a = TerminalQrRenderer.Render(new Uri("https://a.example.com"));
        string b = TerminalQrRenderer.Render(new Uri("https://b.example.com"));
        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task Render_ThrowsArgumentNullException_ForNullUri()
    {
        await Assert.That(() => TerminalQrRenderer.Render(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Render_ContainsNewlines_ForMultiLineOutput()
    {
        string result = TerminalQrRenderer.Render(new Uri("https://example.com"));
        await Assert.That(result).Contains("\n");
    }

    [Test]
    public async Task Render_ShortPayload_UsesV2Matrix()
    {
        string result = TerminalQrRenderer.Render(new Uri("harbor://127.0.0.1:48710#abc"));
        string[] lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // v2 → 25 modules wide, (25+1)/2 = 13 rows.
        await Assert.That(lines.All(l => l.Length == 25)).IsTrue();
        await Assert.That(lines.Length).IsEqualTo(13);
    }

    [Test]
    public async Task Render_LongPairingUri_UsesV4Matrix()
    {
        // A realistic pairing code (~60 chars) must not truncate: it upgrades
        // to the v4 matrix (33 modules, 17 rows).
        string psk = new('x', 22);
        string code = $"harbor://dell.tail1234.ts.net:48710#{psk}";
        await Assert.That(code.Length).IsGreaterThan(40);

        string result = TerminalQrRenderer.Render(new Uri(code));
        string[] lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.All(l => l.Length == 33)).IsTrue();
        await Assert.That(lines.Length).IsEqualTo(17);
    }
}
