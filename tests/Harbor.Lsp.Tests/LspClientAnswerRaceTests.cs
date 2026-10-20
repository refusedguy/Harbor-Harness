using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Lsp.Tests;

/// <summary>
///     The request/response demux must not lose an answer that arrives before
///     the requester has finished its own bookkeeping.
/// </summary>
/// <remarks>
///     The client used to register its pending waiter AFTER the request frame
///     had gone out. A server fast enough to answer while the writer was still
///     inside the write had its answer discarded by the reader (no entry for
///     the id), and the requester - whose wait was unbounded - then waited
///     forever. That is the hang CI saw: the fake python server answers in
///     microseconds, and under instrumentation the writer's continuation is
///     slow enough for the answer to win the race.
/// </remarks>
public class LspClientAnswerRaceTests
{
    [Test]
    public async Task Answer_That_Arrives_Before_The_Writer_Returns_Is_Not_Dropped()
    {
        using var pipe = new EagerAnswerPipe();
        await using var client = new LspClient(pipe.ToClient, pipe.FromClient, NullLogger.Instance);
        client.ServerNotification += (_, e) =>
        {
            if (e.Method == "e2e/ping") pipe.MarkAnswerDrained();
        };
        client.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        JsonElement? result = await client.SendRequestAsync("textDocument/definition", null, cts.Token);

        await Assert.That(result is not null).IsTrue();
        await Assert.That(result!.Value.GetProperty("ok").GetBoolean()).IsTrue();
    }
}

