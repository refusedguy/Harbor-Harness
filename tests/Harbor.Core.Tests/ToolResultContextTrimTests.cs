using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Application.Sessions;
namespace Harbor.Core.Tests;
/// <summary>
///     Tests for the #235 model-context cap on the tool-result → LLM path:
///     giant payloads are trimmed head-kept/tail-cut with a marker in
///     <see cref="MessageConverter.ToLlmMessages" />, short payloads pass
///     through untouched, and the domain message keeps the full text.
/// </summary>
public class ToolResultContextTrimTests
{
    private static readonly DateTimeOffset Ts = DateTimeOffset.UtcNow;

    private static readonly string SessionId = "session-trim";

    private static ToolResultMessage Results(params ToolResultEntry[] entries) =>
        new("m-trim", SessionId, Ts, entries);

    [Test]
    public async Task ToLlmMessages_LongPayload_CappedWithMarker()
    {
        var converter = new MessageConverter();
        string payload = new string('x', ToolResultContextTrim.MaxToolResultChars + 500);

        var result = converter.ToLlmMessages(new AgentMessage[]
        {
            Results(new ToolResultEntry("call-1", "bash", payload, true))
        });

        await Assert.That(result.Count).IsEqualTo(1);
        var llm = (LlmToolResultMessage)result[0];
        await Assert.That(llm.IsError).IsTrue();
        await Assert.That(llm.Output.Length).IsLessThan(payload.Length);
        await Assert.That(llm.Output).Contains("truncated");
        await Assert.That(llm.Output).Contains("session log");
    }

    [Test]
    public async Task ToLlmMessages_LongPayload_HeadPreserved()
    {
        var converter = new MessageConverter();
        string head = "Error: 429 Too Many Requests — retry after 30s. ";
        string payload = head + new string('m', ToolResultContextTrim.MaxToolResultChars);

        var result = converter.ToLlmMessages(new AgentMessage[]
        {
            Results(new ToolResultEntry("call-1", "webfetch", payload, true))
        });

        var llm = (LlmToolResultMessage)result[0];
        await Assert.That(llm.Output.StartsWith(head)).IsTrue();
        await Assert.That(llm.Output.StartsWith(payload.Substring(0, ToolResultContextTrim.MaxToolResultChars))).IsTrue();
    }

    [Test]
    public async Task ToLlmMessages_ShortPayload_PassesThrough()
    {
        var converter = new MessageConverter();
        const string payload = "file contents here";

        var result = converter.ToLlmMessages(new AgentMessage[]
        {
            Results(new ToolResultEntry("call-1", "read", payload, false))
        });

        var llm = (LlmToolResultMessage)result[0];
        await Assert.That(llm.Output).IsEqualTo(payload);
        await Assert.That(llm.IsError).IsFalse();
    }

    [Test]
    public async Task ToLlmMessages_BoundaryPayload_PassesThrough()
    {
        var converter = new MessageConverter();
        string payload = new string('y', ToolResultContextTrim.MaxToolResultChars);

        var result = converter.ToLlmMessages(new AgentMessage[]
        {
            Results(new ToolResultEntry("call-1", "read", payload, false))
        });

        var llm = (LlmToolResultMessage)result[0];
        await Assert.That(llm.Output).IsEqualTo(payload);
    }

    [Test]
    public async Task ToLlmMessages_TrimmedPayload_DomainKeepsFullText()
    {
        var converter = new MessageConverter();
        string payload = new string('z', ToolResultContextTrim.MaxToolResultChars + 100);
        var domain = Results(new ToolResultEntry("call-1", "bash", payload, true));

        _ = converter.ToLlmMessages(new AgentMessage[] { domain });

        await Assert.That(domain.Results[0].Output).IsEqualTo(payload);
        await Assert.That(domain.Results[0].Output.Length).IsEqualTo(payload.Length);
    }
}
