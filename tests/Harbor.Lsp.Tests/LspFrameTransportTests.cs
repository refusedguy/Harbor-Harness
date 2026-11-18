using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Lsp.Tests;

/// <summary>
///     The framing half of the wire protocol is testable on its own (#2613):
///     <see cref="LspFrameTransport" /> speaks Content-Length over any two
///     streams, with no process and no request correlation involved.
/// </summary>
public class LspFrameTransportTests
{
    private static async Task<byte[]> ReadExactlyAsync(MemPipeStream input, int count)
    {
        var buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int read = await input.ReadAsync(buffer.AsMemory(total), CancellationToken.None);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }

        return buffer;
    }

    [Test]
    public async Task WriteFrame_HeaderLengthMatchesBody()
    {
        var toServer = new MemPipeStream();
        await using var transport = new LspFrameTransport(new MemPipeStream(), toServer);
        byte[] payload = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"ping"}""");

        await transport.WriteFrameAsync(payload, CancellationToken.None);

        byte[] expectedHeader = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        byte[] written = await ReadExactlyAsync(toServer, expectedHeader.Length + payload.Length);
        await Assert.That(written.AsSpan(0, expectedHeader.Length).SequenceEqual(expectedHeader)).IsTrue();
        await Assert.That(written.AsSpan(expectedHeader.Length).SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task ReadFrame_ParsesBodyThroughSourceGeneratedContext()
    {
        var fromServer = new MemPipeStream();
        await using var transport = new LspFrameTransport(fromServer, new MemPipeStream());
        byte[] body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":7,"result":{"ok":true}}""");
        byte[] frame = [.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body];
        await fromServer.WriteAsync(frame, CancellationToken.None);

        JsonElement? parsed = await transport.ReadFrameAsync(CancellationToken.None);

        await Assert.That(parsed is not null).IsTrue();
        await Assert.That(parsed!.Value.GetProperty("id").GetInt32()).IsEqualTo(7);
        await Assert.That(parsed.Value.GetProperty("result").GetProperty("ok").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task ReadFrame_HeaderParsingKeepsCaseWhitespaceAndPlusSign()
    {
        var fromServer = new MemPipeStream();
        await using var transport = new LspFrameTransport(fromServer, new MemPipeStream());
        byte[] body = Encoding.UTF8.GetBytes("{}");
        byte[] frame = [.. Encoding.ASCII.GetBytes($"content-length: +{body.Length} \r\n\r\n"), .. body];
        await fromServer.WriteAsync(frame, CancellationToken.None);

        JsonElement? parsed = await transport.ReadFrameAsync(CancellationToken.None);

        await Assert.That(parsed is not null).IsTrue();
        await Assert.That(parsed!.Value.ValueKind).IsEqualTo(JsonValueKind.Object);
    }

    [Test]
    public async Task ReadFrame_MissingContentLength_ThrowsFormatException()
    {
        var fromServer = new MemPipeStream();
        await using var transport = new LspFrameTransport(fromServer, new MemPipeStream());
        await fromServer.WriteAsync(Encoding.ASCII.GetBytes("Content-Type: application/vscode-jsonrpc\r\n\r\n"), CancellationToken.None);

        await Assert.ThrowsAsync<FormatException>(async () =>
            await transport.ReadFrameAsync(CancellationToken.None));
    }

    [Test]
    public async Task ReadFrame_MalformedBody_ThrowsJsonException()
    {
        var fromServer = new MemPipeStream();
        await using var transport = new LspFrameTransport(fromServer, new MemPipeStream());
        byte[] body = Encoding.UTF8.GetBytes("not-json");
        byte[] frame = [.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body];
        await fromServer.WriteAsync(frame, CancellationToken.None);

        await Assert.ThrowsAsync<JsonException>(async () =>
            await transport.ReadFrameAsync(CancellationToken.None));
    }
}

/// <summary>
///     The client keeps request-id correlation and notification dispatch when
///     its transport is injected (#2613): no streams and no server are needed
///     to drive the demux half of the protocol.
/// </summary>
public class LspClientTransportTests
{
    [Test]
    public async Task InjectedTransport_RequestDemuxAndNotification_WorkWithoutStreams()
    {
        var transport = new FakeFrameTransport();
        await using var client = new LspClient(transport, NullLogger.Instance);
        client.Start();

        var received = new TaskCompletionSource<LspNotificationEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ServerNotification += (_, args) => received.TrySetResult(args);

        Task<JsonElement?> request = client.SendRequestAsync("textDocument/definition", null, CancellationToken.None);
        using (JsonDocument envelope = JsonDocument.Parse(transport.Written[0]))
        {
            await Assert.That(envelope.RootElement.GetProperty("jsonrpc").GetString()).IsEqualTo("2.0");
            await Assert.That(envelope.RootElement.GetProperty("method").GetString()).IsEqualTo("textDocument/definition");
            int id = envelope.RootElement.GetProperty("id").GetInt32();
            transport.Feed("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"ok\":true}}");
        }

        JsonElement? result = await request.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result!.Value.GetProperty("ok").GetBoolean()).IsTrue();

        transport.Feed("""{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{}}""");
        LspNotificationEventArgs args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(args.Method).IsEqualTo("textDocument/publishDiagnostics");

        await client.SendNotificationAsync("textDocument/didOpen", null, CancellationToken.None);
        using (JsonDocument notification = JsonDocument.Parse(transport.Written[1]))
        {
            await Assert.That(notification.RootElement.TryGetProperty("id", out _)).IsFalse();
        }
    }

    private sealed class FakeFrameTransport : ILspFrameTransport
    {
        private readonly Channel<JsonElement> _incoming = Channel.CreateUnbounded<JsonElement>();

        public List<string> Written { get; } = [];

        public Task WriteFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Written.Add(Encoding.UTF8.GetString(payload.Span));
            return Task.CompletedTask;
        }

        public async ValueTask<JsonElement?> ReadFrameAsync(CancellationToken ct)
            => await _incoming.Reader.ReadAsync(ct).ConfigureAwait(false);

        public void Feed(string json)
        {
            using var doc = JsonDocument.Parse(json);
            _incoming.Writer.TryWrite(doc.RootElement.Clone());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
