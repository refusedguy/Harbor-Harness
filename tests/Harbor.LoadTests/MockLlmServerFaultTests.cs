using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.E2E.Framework;
using Harbor.Providers.OpenAiCompatible;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;

namespace Harbor.LoadTests;

/// <summary>
///     Fault injection at the mock's HTTP/SSE boundary. The real
///     OpenAI-compatible client streams over a real <see cref="MockLlmServer" />
///     here — no scripted <c>HttpMessageHandler</c>, no live API key — and must
///     survive a bad line, a missing sentinel, a truncated tail and a mid-stream
///     close without emitting a terminal error or losing the text it already
///     received.
/// </summary>
public class MockLlmServerFaultTests
{
    private const string Text = "hello world";

    private static async Task<List<LlmEvent>> StreamWithFaultAsync(MockLlmFault fault)
    {
        await using var server = new MockLlmServer();
        await server.StartAsync();
        server.SetChunkDelay(TimeSpan.Zero);
        server.SetResponse(LoadTestFakes.TestModel.Id, Text);
        server.SetFault(fault);

        using var http = new HttpClient();
        var client = new OpenAiCompatibleLlmClient(
            http,
            LoadTestFakes.MockProvider(server.BaseUri),
            new TestAuthResolver("fault-test-key"),
            new StaticModelCatalog(),
            NullLogger<OpenAiCompatibleLlmClient>.Instance);

        var events = new List<LlmEvent>();
        await foreach (LlmEvent evt in client.StreamAsync(new LlmRequest(
            LoadTestFakes.TestModel.Id, [LlmUserMessage.Text("go")], "", [])))
        {
            events.Add(evt);
        }
        return events;
    }

    private static string ReceivedText(List<LlmEvent> events) =>
        string.Concat(events.OfType<TextDeltaEvent>().Select(static t => t.Delta));

    private static async Task AssertCleanFinishAsync(List<LlmEvent> events)
    {
        await Assert.That(events.Count(e => e is ErrorEvent)).IsEqualTo(0);
        await Assert.That(events.Count(e => e is FinishEvent)).IsEqualTo(1);
    }

    [Test]
    public async Task BadJsonLine_IsSkipped_AndBothSidesOfItSurvive()
    {
        List<LlmEvent> events = await StreamWithFaultAsync(MockLlmFault.BadJson);

        await Assert.That(ReceivedText(events)).IsEqualTo(Text);
        await AssertCleanFinishAsync(events);
    }

    [Test]
    public async Task MissingDoneSentinel_StillEmitsOneFinishAtEof()
    {
        List<LlmEvent> events = await StreamWithFaultAsync(MockLlmFault.NoDone);

        await Assert.That(ReceivedText(events)).IsEqualTo(Text);
        await AssertCleanFinishAsync(events);
    }

    [Test]
    public async Task TruncatedFinalSseLine_IsSkipped_AndStreamFinishes()
    {
        List<LlmEvent> events = await StreamWithFaultAsync(MockLlmFault.TruncatedSse);

        await Assert.That(ReceivedText(events)).IsEqualTo(Text);
        await AssertCleanFinishAsync(events);
    }

    [Test]
    public async Task DisconnectAfterChunk_KeepsDeliveredText_AndFinishesAtEof()
    {
        List<LlmEvent> events = await StreamWithFaultAsync(MockLlmFault.DisconnectAfterChunk);

        // The first 4-char chunk is the only one on the wire; the cut must not
        // turn into an error, and EOF must still close the stream exactly once.
        await Assert.That(ReceivedText(events)).IsEqualTo("hell");
        await AssertCleanFinishAsync(events);
    }
}
