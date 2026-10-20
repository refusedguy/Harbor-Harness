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
    // #1770: the parse vectors differed only in the command line and the
    // expected tokens — the asserts never changed. The command is the table
    // and the expected tokens are one '|'-separated string, because an
    // attribute argument cannot carry an implicitly-typed array. Every row
    // keeps its original test name as the display name.
    [Test]
    [Arguments("npx -y pkg", "npx|-y|pkg", DisplayName = "Parse_SimpleCommand_SplitsOnWhitespace")]
    [Arguments("node script.js --flag value", "node|script.js|--flag|value", DisplayName = "Parse_FlagWithValue_ProducesSeparateTokens")]
    [Arguments("node \"/path/with space/x.js\" --port 3000", "node|/path/with space/x.js|--port|3000", DisplayName = "Parse_DoubleQuotedPathWithSpaces_IsSingleArgument")]
    [Arguments("sh -c 'echo \"hello world\"'", "sh|-c|echo \"hello world\"", DisplayName = "Parse_SingleQuotedArgument_IsLiteral")]
    [Arguments("\"a \\\"b\\\" c\"", "a \"b\" c", DisplayName = "Parse_EscapedQuotesInsideDoubleQuotes_Unescape")]
    [Arguments("cmd \"C:\\\\temp\\\\x y\"", "cmd|C:\\temp\\x y", DisplayName = "Parse_EscapedBackslashInsideDoubleQuotes_Unescapes")]
    [Arguments("run arg\\ with\\ spaces", "run|arg with spaces", DisplayName = "Parse_BackslashOutsideQuotes_EscapesNextChar")]
    [Arguments("npx\t  -y \t pkg", "npx|-y|pkg", DisplayName = "Parse_RepeatedWhitespace_Collapses")]
    [Arguments("cmd 'a b' \"c d\" e", "cmd|a b|c d|e", DisplayName = "Parse_MixedQuoting_TokensAreIndependent")]
    [Arguments("foo", "foo", DisplayName = "Parse_SingleToken_HasNoArgs")]
    public async Task Parse_Command_SplitsOnWhitespace(string command, string expectedTokens)
    {
        var result = McpArgvParser.ParseCommand(command);
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEquivalentTo(expectedTokens.Split('|'));
    }

    [Test]
    [Arguments("", DisplayName = "Parse_EmptyString_Fails")]
    [Arguments("   \t  ", DisplayName = "Parse_WhitespaceOnly_Fails")]
    public async Task Parse_BlankCommand_Fails(string command)
    {
        var result = McpArgvParser.ParseCommand(command);
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    [Arguments("cmd \"never closed", "double", DisplayName = "Parse_UnterminatedDoubleQuote_FailsWithPosition")]
    [Arguments("cmd 'never closed", "single", DisplayName = "Parse_UnterminatedSingleQuote_FailsWithPosition")]
    public async Task Parse_UnterminatedQuote_FailsWithPosition(string command, string expectedErrorFragment)
    {
        var result = McpArgvParser.ParseCommand(command);
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains(expectedErrorFragment);
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
    public async Task Register_LegacyQuotedCommand_SucceedsAndParses()
    {
        var registry = new McpRegistry(NullLogger<McpRegistry>.Instance, McpTransportResolver.Default);
        var result = registry.Register("quoted", "npx -y 'srv foo'");
        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Register_BlankLegacyCommand_Fails()
    {
        var registry = new McpRegistry(NullLogger<McpRegistry>.Instance, McpTransportResolver.Default);
        var result = registry.Register("blank", "   ");
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("empty");
    }
}
