using System.Text;
using System.Text.Json;

namespace Harbor.Lsp.Tests;

/// <summary>
///     The client-to-server half of a duplex pipe that answers a request the
///     moment the full request frame reaches the wire, and holds the writer
///     until the client has drained the answer.
/// </summary>
/// <remarks>
///     <para>
///         This is the shape that turned a CI job into a hang. The client used
///         to register its pending waiter AFTER the write, so an answer that
///         arrived while the writer was still inside the write was dropped on
///         the floor: the reader found no entry for the id, discarded the
///         frame, and the requester waited for a reply that had already been
///         thrown away. The wait was unbounded, so a lost answer was a hung
///         session rather than a failed one.
///     </para>
///     <para>
///         The pipe makes that window deterministic instead of probabilistic:
///         the answer is produced from inside the write, and the write does
///         not return until the client has read the answer AND the follow-up
///         notification that comes after it. The client's read loop handles
///         frames in order, so the notification proves the answer was already
///         processed - and the requester is provably still inside its write
///         when that happens.
///     </para>
/// </remarks>
internal sealed class EagerAnswerPipe : Stream
{
    private const string PingMethod = "e2e/ping";

    private readonly MemPipeStream _toClient = new();
    private readonly List<byte> _request = [];
    private readonly TaskCompletionSource<bool> _answerDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _answered;

    /// <summary>The stream the client READS from (server to client).</summary>
    public Stream ToClient => _toClient;

    /// <summary>The stream the client WRITES to (client to server).</summary>
    public Stream FromClient => this;

    /// <summary>
    ///     Called by the test when the client has observed the follow-up
    ///     notification - the proof that the answer was already processed.
    /// </summary>
    public void MarkAnswerDrained() => _answerDrained.TrySetResult(true);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _request.AddRange(buffer.ToArray());
        if (!TryTakeCompleteRequest(out int id)) return;

        // The answer goes out BEFORE this write returns: the client is still
        // inside its own write call, which is the window that used to lose it.
        await WriteFrameAsync(_toClient, Encode(new { jsonrpc = "2.0", id, result = new { ok = true } }), cancellationToken)
            .ConfigureAwait(false);
        await WriteFrameAsync(_toClient, Encode(new { jsonrpc = "2.0", method = PingMethod, @params = new { } }), cancellationToken)
            .ConfigureAwait(false);

        // Hold the writer until the client has read the answer AND the
        // notification that follows it.
        await _answerDrained.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toClient.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>True once a complete request frame has been written (one request).</summary>
    private bool TryTakeCompleteRequest(out int id)
    {
        id = 0;
        if (_answered != 0) return false;

        byte[] bytes = [.. _request];
        int headerEnd = IndexOfHeaderEnd(bytes);
        if (headerEnd < 0) return false;

        int contentLength = ParseContentLength(bytes, headerEnd);
        if (bytes.Length < headerEnd + contentLength) return false;

        string body = Encoding.UTF8.GetString(bytes, headerEnd, contentLength);
        using JsonDocument document = JsonDocument.Parse(body);
        id = document.RootElement.GetProperty("id").GetInt32();
        _answered = id;
        return true;
    }

    private static int IndexOfHeaderEnd(byte[] bytes)
    {
        for (int i = 0; i + 3 < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r' && bytes[i + 1] == (byte)'\n'
                && bytes[i + 2] == (byte)'\r' && bytes[i + 3] == (byte)'\n')
            {
                return i + 4;
            }
        }

        return -1;
    }

    private static int ParseContentLength(byte[] bytes, int headerEnd)
    {
        string headers = Encoding.ASCII.GetString(bytes, 0, headerEnd);
        foreach (string line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["Content-Length:".Length..].Trim(), out int length))
            {
                return length;
            }
        }

        throw new FormatException("no Content-Length header in " + headers);
    }

    private static byte[] Encode(object message) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

    private static async Task WriteFrameAsync(MemPipeStream output, byte[] body, CancellationToken ct)
    {
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(body, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }
}
