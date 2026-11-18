using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     F14: <c>harbor ask --prompt-link &lt;url&gt;</c> turns a shareable deep
///     link into the one-shot prompt. The parser is pure and scheme-agnostic:
///     it only reads the <c>prompt</c> query value and never contacts the URL.
/// </summary>
public class PromptLinkTests
{
    [Test]
    public async Task ParsePromptLink_DecodesPercentAndPlus()
    {
        string? prompt = CliArgs.ParsePromptLink("https://example.com/new?prompt=Explain%20this+diff");

        await Assert.That(prompt).IsEqualTo("Explain this diff");
    }

    [Test]
    public async Task ParsePromptLink_ReadsPromptAmongOtherParameters()
    {
        string? prompt = CliArgs.ParsePromptLink("https://harbor.dev/new?utm=share&PROMPT=hello%2Bworld&mode=code");

        await Assert.That(prompt).IsEqualTo("hello+world");
    }

    [Test]
    public async Task ParsePromptLink_StopsAtFragment()
    {
        string? prompt = CliArgs.ParsePromptLink("https://harbor.dev/new?prompt=one#two");

        await Assert.That(prompt).IsEqualTo("one");
    }

    [Test]
    public async Task ParsePromptLink_AcceptsSchemeRelativeLink()
    {
        string? prompt = CliArgs.ParsePromptLink("/new?prompt=from%20history");

        await Assert.That(prompt).IsEqualTo("from history");
    }

    [Test]
    public async Task ParsePromptLink_NoPromptParameter_ReturnsNull()
    {
        await Assert.That(CliArgs.ParsePromptLink("https://harbor.dev/new?mode=code")).IsNull();
        await Assert.That(CliArgs.ParsePromptLink("https://harbor.dev/new")).IsNull();
        await Assert.That(CliArgs.ParsePromptLink("https://harbor.dev/new?")).IsNull();
    }

    [Test]
    public async Task ParsePromptLink_BlankPrompt_ReturnsNull()
    {
        await Assert.That(CliArgs.ParsePromptLink("https://harbor.dev/new?prompt=%20%20")).IsNull();
        await Assert.That(CliArgs.ParsePromptLink("https://harbor.dev/new?prompt=")).IsNull();
    }

    [Test]
    public async Task ParsePromptLink_EmptyUrl_ReturnsNull()
    {
        await Assert.That(CliArgs.ParsePromptLink(null)).IsNull();
        await Assert.That(CliArgs.ParsePromptLink("")).IsNull();
        await Assert.That(CliArgs.ParsePromptLink("   ")).IsNull();
    }
}
