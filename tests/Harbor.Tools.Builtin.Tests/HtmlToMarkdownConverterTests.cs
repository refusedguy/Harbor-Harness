using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Tests for <see cref="HtmlToMarkdownConverter" /> in isolation — pure
///     static HTML → markdown conversion, no HTTP, no tool wiring.
/// </summary>
public class HtmlToMarkdownConverterTests
{
    [Test]
    public async Task Convert_EmptyInput_ReturnsEmpty()
    {
        await Assert.That(HtmlToMarkdownConverter.Convert(string.Empty)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Convert_HeadingAndLink_ProducesMarkdown()
    {
        string html = "<html><body><h1>Title</h1><p>Hello <a href=\"https://example.com\">world</a>.</p></body></html>";

        string md = HtmlToMarkdownConverter.Convert(html);

        await Assert.That(md).Contains("# Title");
        await Assert.That(md).Contains("[world](https://example.com)");
    }

    [Test]
    public async Task Convert_PreBlock_PreservedAsFencedCode()
    {
        string html = "<pre><code>var x = 1;</code></pre>";

        string md = HtmlToMarkdownConverter.Convert(html);

        await Assert.That(md).Contains("```");
        await Assert.That(md).Contains("var x = 1;");
    }

    [Test]
    public async Task Convert_ScriptAndStyle_Stripped()
    {
        string html = "<p>keep</p><script>alert(1)</script><style>p{color:red}</style>";

        string md = HtmlToMarkdownConverter.Convert(html);

        await Assert.That(md).Contains("keep");
        await Assert.That(md.Contains("alert(1)")).IsFalse();
        await Assert.That(md.Contains("color:red")).IsFalse();
    }

    [Test]
    public async Task Convert_PlainText_PassesThrough()
    {
        await Assert.That(HtmlToMarkdownConverter.Convert("just text")).IsEqualTo("just text");
    }
}
