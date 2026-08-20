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
}
