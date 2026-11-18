using Harbor.Providers.Ollama;

namespace Harbor.Providers.Tests;

/// <summary>
///     #2678 item 92: models without native tool calling fall back to a
///     dollar-prefixed command line or a fenced <c>execute_javascript</c> /
///     <c>list_functions</c> block. These tests pin the recovered shape: tool
///     name, argument object, ordering, and the refusal to emit a partial call.
/// </summary>
public class LegacyToolCallParserTests
{
    private readonly ILegacyToolCallParser _parser = new LegacyToolCallParser();

    [Test]
    public async Task Parse_NullOrBlankText_EmitsNothing()
    {
        await Assert.That(_parser.Parse(null)).IsEmpty();
        await Assert.That(_parser.Parse("   ")).IsEmpty();
        await Assert.That(_parser.Parse("\n\n")).IsEmpty();
    }

    [Test]
    public async Task Parse_PlainText_EmitsNothing()
    {
        var calls = _parser.Parse("I cannot call tools, but here is an answer.");

        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task Parse_DollarCommandLine_EmitsBashCall()
    {
        var calls = _parser.Parse("$ echo hello");

        var call = calls.Single();
        await Assert.That(call.ToolName).IsEqualTo("bash");
        await Assert.That(call.ArgumentsJson).IsEqualTo("""{"command":"echo hello"}""");
    }

    [Test]
    public async Task Parse_DollarCommandLine_ToleratesLeadingWhitespace()
    {
        var calls = _parser.Parse("   $   ls -la   ");

        var call = calls.Single();
        await Assert.That(call.ArgumentsJson).IsEqualTo("""{"command":"ls -la"}""");
    }

    [Test]
    public async Task Parse_QuotesInCommand_AreEscaped()
    {
        var calls = _parser.Parse("""$ echo "hi there" """);

        var call = calls.Single();
        await Assert.That(call.ArgumentsJson).IsEqualTo("""{"command":"echo \"hi there\""}""");
    }

    [Test]
    public async Task Parse_MultipleDollarLines_KeepSourceOrder()
    {
        var calls = _parser.Parse("$ first\n$ second\n$ third");

        await Assert.That(calls.Select(c => c.ToolName)).IsEquivalentTo(new[] { "bash", "bash", "bash" });
        await Assert.That(calls[0].ArgumentsJson).IsEqualTo("""{"command":"first"}""");
        await Assert.That(calls[1].ArgumentsJson).IsEqualTo("""{"command":"second"}""");
        await Assert.That(calls[2].ArgumentsJson).IsEqualTo("""{"command":"third"}""");
    }

    [Test]
    public async Task Parse_ExecuteJavascriptFence_EmitsCallWithBody()
    {
        const string text = """
            Some preamble.
            ```execute_javascript
            return 1 + 1;
            return 3;
            ```
            """;

        var calls = _parser.Parse(text);

        var call = calls.Single();
        await Assert.That(call.ToolName).IsEqualTo("execute_javascript");
        await Assert.That(call.ArgumentsJson).IsEqualTo("""{"code":"return 1 + 1;\nreturn 3;"}""");
    }

    [Test]
    public async Task Parse_ListFunctionsFence_EmitsCallWithBody()
    {
        const string text = """
            ```list_functions
            ["read", "write"]
            ```
            """;

        var calls = _parser.Parse(text);

        var call = calls.Single();
        await Assert.That(call.ToolName).IsEqualTo("list_functions");
        await Assert.That(call.ArgumentsJson).IsEqualTo("""{"code":"[\"read\", \"write\"]"}""");
    }

    [Test]
    public async Task Parse_DollarLineInsideFence_BelongsToFenceBody()
    {
        const string text = """
            ```execute_javascript
            $ not a shell command
            ```
            """;

        var calls = _parser.Parse(text);

        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0].ToolName).IsEqualTo("execute_javascript");
        await Assert.That(calls[0].ArgumentsJson).IsEqualTo("""{"code":"$ not a shell command"}""");
    }

    [Test]
    public async Task Parse_UnknownFence_IsIgnored()
    {
        const string text = """
            ```python
            print("hello")
            ```
            """;

        await Assert.That(_parser.Parse(text)).IsEmpty();
    }

    [Test]
    public async Task Parse_UnterminatedFence_EmitsNothing()
    {
        const string text = """
            ```execute_javascript
            return 42;
            """;

        await Assert.That(_parser.Parse(text)).IsEmpty();
    }

    [Test]
    public async Task Parse_MixedMarkers_EmitsInSourceOrder()
    {
        const string text = """
            $ echo start
            ```list_functions
            ["read"]
            ```
            $ echo done
            """;

        var calls = _parser.Parse(text);

        await Assert.That(calls.Count).IsEqualTo(3);
        await Assert.That(calls[0].ToolName).IsEqualTo("bash");
        await Assert.That(calls[1].ToolName).IsEqualTo("list_functions");
        await Assert.That(calls[2].ToolName).IsEqualTo("bash");
        await Assert.That(calls[2].ArgumentsJson).IsEqualTo("""{"command":"echo done"}""");
    }
}
