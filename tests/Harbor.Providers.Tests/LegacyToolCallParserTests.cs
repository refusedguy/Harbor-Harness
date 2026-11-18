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
    private readonly LegacyToolCallParser _parser = new();

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
        var parsed = _parser.Parse("$ echo hello");
        var first = parsed[0];

        await Assert.That(first.ToolName).IsEqualTo("bash");
        await Assert.That(first.ArgumentsJson).IsEqualTo("""{"command":"echo hello"}""");
    }

    [Test]
    public async Task Parse_DollarCommandLine_ToleratesLeadingWhitespace()
    {
        await Assert.That(_parser.Parse("   $   ls -la   ")[0].ArgumentsJson)
            .IsEqualTo("""{"command":"ls -la"}""");
    }

    [Test]
    public async Task Parse_QuotesInCommand_AreEscaped()
    {
        await Assert.That(_parser.Parse("""$ echo "hi there" """)[0].ArgumentsJson)
            .IsEqualTo("""{"command":"echo \"hi there\""}""");
    }

    [Test]
    public async Task Parse_MultipleDollarLines_KeepSourceOrder()
    {
        var calls = _parser.Parse("$ first\n$ second\n$ third");

        await Assert.That(calls.Count).IsEqualTo(3);
        await Assert.That(calls[0].ArgumentsJson).IsEqualTo("""{"command":"first"}""");
        await Assert.That(calls[1].ArgumentsJson).IsEqualTo("""{"command":"second"}""");
        await Assert.That(calls[2].ArgumentsJson).IsEqualTo("""{"command":"third"}""");
    }

    [Test]
    public async Task Parse_KnownFences_EmitsCallsWithBody()
    {
        var text = """
            ```execute_javascript
            return 1 + 1;
            ```
            ```list_functions
            ["read", "write"]
            ```
            """;
        var calls = _parser.Parse(text);

        await Assert.That(calls.Count).IsEqualTo(2);
        await Assert.That(calls[0].ToolName).IsEqualTo("execute_javascript");
        await Assert.That(calls[0].ArgumentsJson).IsEqualTo("""{"code":"return 1 + 1;"}""");
        await Assert.That(calls[1].ToolName).IsEqualTo("list_functions");
        await Assert.That(calls[1].ArgumentsJson).IsEqualTo("""{"code":"[\"read\", \"write\"]"}""");
    }

    [Test]
    public async Task Parse_DollarLineInsideFence_BelongsToFenceBody()
    {
        var text = """
            ```execute_javascript
            $ not a shell command
            ```
            """;

        await Assert.That(_parser.Parse(text).Single().ArgumentsJson)
            .IsEqualTo("""{"code":"$ not a shell command"}""");
    }

    [Test]
    public async Task Parse_UnknownFence_IsIgnored()
    {
        var text = """
            ```python
            print("hello")
            ```
            """;

        await Assert.That(_parser.Parse(text)).IsEmpty();
    }

    [Test]
    public async Task Parse_UnterminatedFence_EmitsNothing()
    {
        await Assert.That(_parser.Parse("```execute_javascript\nreturn 42;")).IsEmpty();
    }

    [Test]
    public async Task Parse_MixedMarkers_EmitsInSourceOrder()
    {
        var text = """
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
