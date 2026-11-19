using System.Text;
using Harbor.Abstractions.Events;
using Harbor.Providers.Ollama;

namespace Harbor.Providers.Tests;

/// <summary>
///     #3404: LegacyToolCallParser is wired into the Ollama response path, not
///     left as an isolated leaf. These tests feed NDJSON chunks to the real
///     <see cref="OllamaLlmClient.MapNdjsonChunk" /> parser and assert the
///     observable events, so a revert of the wiring fails them.
/// </summary>
public class OllamaLegacyToolCallTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Test]
    public async Task MapNdjsonChunk_DollarCommandContent_EmitsToolCall()
    {
        var events = OllamaLlmClient.MapNdjsonChunk(
            Utf8("""{"message":{"content":"$ ls -la"}}"""),
            new Dictionary<int, string>());

        var start = events.OfType<ToolCallStartEvent>().Single();
        await Assert.That(start.ToolName).IsEqualTo("bash");
        var delta = events.OfType<ToolCallDeltaEvent>().Single();
        await Assert.That(delta.Id).IsEqualTo(start.Id);
        await Assert.That(delta.ArgsDelta).IsEqualTo("""{"command":"ls -la"}""");

        // The text WAS the call's encoding; it must not leak into the answer.
        await Assert.That(events.OfType<TextDeltaEvent>().Count()).IsEqualTo(0);
    }

    [Test]
    public async Task MapNdjsonChunk_FencedLegacyContent_EmitsToolCall()
    {
        var events = OllamaLlmClient.MapNdjsonChunk(
            Utf8("""{"message":{"content":"```execute_javascript\nreturn 1 + 1;\n```"}}"""),
            new Dictionary<int, string>());

        await Assert.That(events.OfType<ToolCallStartEvent>().Single().ToolName)
            .IsEqualTo("execute_javascript");
        await Assert.That(events.OfType<ToolCallDeltaEvent>().Single().ArgsDelta)
            .IsEqualTo("""{"code":"return 1 + 1;"}""");
    }

    [Test]
    public async Task MapNdjsonChunk_MultipleLegacyCalls_KeepOrderAndArguments()
    {
        var events = OllamaLlmClient.MapNdjsonChunk(
            Utf8("""{"message":{"content":"$ first\n$ second"}}"""),
            new Dictionary<int, string>());

        var starts = events.OfType<ToolCallStartEvent>().ToList();
        await Assert.That(starts.Count).IsEqualTo(2);
        await Assert.That(starts[0].Id).IsEqualTo("legacy0");
        await Assert.That(starts[1].Id).IsEqualTo("legacy1");

        var deltas = events.OfType<ToolCallDeltaEvent>().ToList();
        await Assert.That(deltas[0].ArgsDelta).IsEqualTo("""{"command":"first"}""");
        await Assert.That(deltas[1].ArgsDelta).IsEqualTo("""{"command":"second"}""");
    }

    [Test]
    public async Task MapNdjsonChunk_RepeatedLegacyCall_ReusesStableId()
    {
        var map = new Dictionary<int, string>();
        byte[] chunk = Utf8("""{"message":{"content":"$ ls"}}""");

        var first = OllamaLlmClient.MapNdjsonChunk(chunk, map);
        var second = OllamaLlmClient.MapNdjsonChunk(chunk, map);

        await Assert.That(first.OfType<ToolCallStartEvent>().Single().Id)
            .IsEqualTo(second.OfType<ToolCallStartEvent>().Single().Id);
    }

    [Test]
    public async Task MapNdjsonChunk_NativeToolCall_KeepsTextAndSkipsLegacyRecovery()
    {
        var events = OllamaLlmClient.MapNdjsonChunk(
            Utf8("""{"message":{"content":"$ echo legacy","tool_calls":[{"index":0,"id":"c1","function":{"name":"read","arguments":"{}"}}]}}"""),
            new Dictionary<int, string>());

        var starts = events.OfType<ToolCallStartEvent>().ToList();
        await Assert.That(starts.Count).IsEqualTo(1);
        await Assert.That(starts[0].ToolName).IsEqualTo("read");
        await Assert.That(events.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("$ echo legacy");
    }

    [Test]
    public async Task MapNdjsonChunk_PlainTextContent_StillEmitsTextDelta()
    {
        var events = OllamaLlmClient.MapNdjsonChunk(
            Utf8("""{"message":{"content":"just an answer"}}"""),
            new Dictionary<int, string>());

        await Assert.That(events.OfType<TextDeltaEvent>().Single().Delta).IsEqualTo("just an answer");
        await Assert.That(events.OfType<ToolCallStartEvent>().Count()).IsEqualTo(0);
    }
}
