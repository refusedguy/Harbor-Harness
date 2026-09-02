using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Tests for <see cref="McpArgvParser" /> — shell-like tokenization of legacy MCP
///     stdio command lines (quoting, escaping, failure modes) plus the
///     <see cref="McpRegistry.Register(string, string)" /> wiring. No processes spawned.
/// </summary>
public class McpArgvParserTests
{
    [Test]
    public async Task Parse_SimpleCommand_SplitsOnWhitespace()
    {
        var result = McpArgvParser.ParseCommand("npx -y pkg");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEquivalentTo(new[] { "npx", "-y", "pkg" });
    }

    [Test]
    public async Task Parse_FlagWithValue_ProducesSeparateTokens()
    {
        var result = McpArgvParser.ParseCommand("node script.js --flag value");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEquivalentTo(new[] { "node", "script.js", "--flag", "value" });
    }

    [Test]
    public async Task Parse_DoubleQuotedPathWithSpaces_IsSingleArgument()
    {
        var result = McpArgvParser.ParseCommand("node \"/path/with space/x.js\" --port 3000");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEquivalentTo(
            new[] { "node", "/path/with space/x.js", "--port", "3000" });
    }

    [Test]
    public async Task Parse_SingleQuotedArgument_IsLiteral()
    {
        var result = McpArgvParser.ParseCommand("""sh -c 'echo "hello world"'""");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEquivalentTo(new[] { "sh", "-c", "echo \"hello world\"" });
    }

    [Test]
    public async Task Parse_EscapedQuotesInsideDoubleQuotes_Unescape()
    {
        var result = McpArgvParser.ParseCommand("\"a \\\"b\\\" c\"");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEquivalentTo(new[] { "a \"b\" c" });
    }

    [Test]
    public async Task Parse_EscapedBackslashInsideDoubleQuotes_Unescapes()
    {
        var result = McpArgvParser.ParseCommand("cmd \"C:\\\\temp\\\\x y\"");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEquivalentTo(new[] { "cmd", "C:\\temp\\x y" });
    }

    [Test]
    public async Task Parse_BackslashOutsideQuotes_EscapesNextChar()
    {
        var result = McpArgvParser.ParseCommand("run arg\\ with\\ spaces");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEquivalentTo(new[] { "run", "arg with spaces" });
    }

    [Test]
    public async Task Parse_EmptyString_Fails()
    {
        var result = McpArgvParser.ParseCommand("");
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task Parse_WhitespaceOnly_Fails()
    {
        var result = McpArgvParser.ParseCommand("   \t  ");
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task Parse_UnterminatedDoubleQuote_FailsWithPosition()
    {
        var result = McpArgvParser.ParseCommand("cmd \"never closed");
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("double");
    }

    [Test]
    public async Task Parse_UnterminatedSingleQuote_FailsWithPosition()
    {
        var result = McpArgvParser.ParseCommand("cmd 'never closed");
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("single");
    }

    [Test]
    public async Task Parse_QuotedEmptyArgument_IsPreserved()
    {
        var result = McpArgvParser.ParseCommand("cmd \"\" --flag");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Length).IsEqualTo(3);
        await Assert.That(result.Value[1]).IsEqualTo("");
    }

    [Test]
    public async Task Parse_RepeatedWhitespace_Collapses()
    {
        var result = McpArgvParser.ParseCommand("npx\t  -y \t pkg");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEquivalentTo(new[] { "npx", "-y", "pkg" });
    }

    [Test]
    public async Task Parse_MixedQuoting_TokensAreIndependent()
    {
        var result = McpArgvParser.ParseCommand("cmd 'a b' \"c d\" e");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEquivalentTo(new[] { "cmd", "a b", "c d", "e" });
    }

    [Test]
    public async Task Parse_SingleToken_HasNoArgs()
    {
        var result = McpArgvParser.ParseCommand("foo");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEquivalentTo(new[] { "foo" });
    }

    [Test]
    public async Task Register_LegacyQuotedCommand_SucceedsAndParses()
    {
        var registry = new McpRegistry(NullLogger<McpRegistry>.Instance);
        var result = registry.Register("quoted", "npx -y 'srv foo'");
        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Register_BlankLegacyCommand_Fails()
    {
        var registry = new McpRegistry(NullLogger<McpRegistry>.Instance);
        var result = registry.Register("blank", "   ");
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("empty");
    }
}
