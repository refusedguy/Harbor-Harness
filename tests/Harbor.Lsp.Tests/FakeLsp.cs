using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Lsp.Tests;

/// <summary>
///     In-memory unidirectional byte pipe with async blocking reads — one
///     instance is one direction of a duplex connection (write into it on one
///     side, read out of it on the other).
/// </summary>
public sealed class MemPipeStream : Stream
{
    private readonly Lock _sync = new();
    private readonly List<byte[]> _chunks = [];
    private TaskCompletionSource<bool> _dataOrClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _chunkIndex;
    private int _offset;
    private bool _closed;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] copy = buffer.ToArray();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _chunks.Add(copy);
            _dataOrClose.TrySetResult(true);
        }

        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task waitTask;
            lock (_sync)
            {
                if (_chunkIndex < _chunks.Count)
                {
                    byte[] chunk = _chunks[_chunkIndex];
                    int n = Math.Min(buffer.Length, chunk.Length - _offset);
                    chunk.AsSpan(_offset, n).CopyTo(buffer.Span);
                    _offset += n;
                    if (_offset == chunk.Length)
                    {
                        _chunkIndex++;
                        _offset = 0;
                    }

                    return n;
                }

                if (_closed) return 0;
                _dataOrClose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waitTask = _dataOrClose.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>Close the pipe: pending and future reads return 0 (EOF).</summary>
    public void End()
    {
        lock (_sync)
        {
            _closed = true;
            _dataOrClose.TrySetResult(true);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
}

/// <summary>
///     In-process fake language server for <see cref="LspClient"/> tests:
///     speaks the same Content-Length protocol over in-memory pipes and
///     answers requests through scriptable delegates.
/// </summary>
public sealed class FakeLspServer : IAsyncDisposable
{
    private readonly MemPipeStream _clientToServer = new();
    private readonly MemPipeStream _serverToClient = new();
    private readonly CancellationTokenSource _cts = new();

    public FakeLspServer()
    {
        Client = new LspClient(_serverToClient, _clientToServer, NullLogger.Instance);
    }

    /// <summary>The client-side endpoint connected to this fake server.</summary>
    public LspClient Client { get; }

    /// <summary>Per-test responder: (method, params) → result object. Null result is sent as <c>result: null</c>.</summary>
    public Func<string, JsonElement?, object?>? OnRequest { get; set; }

    /// <summary>Per-test error injector: (method, params) → error message. Takes precedence over <see cref="OnRequest" />.</summary>
    public Func<string, JsonElement?, string?>? OnError { get; set; }

    /// <summary>Push a raw server notification to the client.</summary>
    public Task NotifyAsync(string method, object? parameters, CancellationToken ct = default)
        => WriteFrameAsync(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        }, _serverToClient, ct);

    /// <summary>Run the request loop (call once).</summary>
    public void Run() => _ = LoopAsync(_cts.Token);

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? body = await ReadFrameAsync(_clientToServer, ct).ConfigureAwait(false);
                if (body is null) return;

                using var doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("method", out JsonElement methodEl)) continue;
                if (!root.TryGetProperty("id", out JsonElement idEl)) continue; // client notification — ignore

                string method = methodEl.GetString()!;
                JsonElement? parameters = root.TryGetProperty("params", out JsonElement p) ? p.Clone() : null;

                string? error = OnError?.Invoke(method, parameters);
                if (error is not null)
                {
                    await WriteFrameAsync(new Dictionary<string, object?>
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = idEl.GetInt32(),
                        ["error"] = new Dictionary<string, object?> { ["code"] = -32603, ["message"] = error },
                    }, _serverToClient, ct).ConfigureAwait(false);
                    continue;
                }

                object? result = OnRequest?.Invoke(method, parameters);
                await WriteFrameAsync(new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idEl.GetInt32(),
                    ["result"] = result,
                }, _serverToClient, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown when the test cancels the pipe.
        }
        catch (IOException)
        {
            // Peer closed the pipe while we were writing.
        }
        catch (JsonException)
        {
            // Client sent malformed content; loop ends.
        }
    }

    private static async Task<string?> ReadFrameAsync(MemPipeStream input, CancellationToken ct)
    {
        var headerBytes = new List<byte>(64);
        while (true)
        {
            var one = new byte[1];
            int n = await input.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) return null;
            headerBytes.Add(one[0]);
            if (headerBytes.Count >= 4
                && headerBytes[^4] == (byte)'\r' && headerBytes[^3] == (byte)'\n'
                && headerBytes[^2] == (byte)'\r' && headerBytes[^1] == (byte)'\n')
                break;
        }

        string header = Encoding.ASCII.GetString([.. headerBytes]);
        int length = -1;
        foreach (string line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(line["Content-Length:".Length..].Trim(), out length);
            }
        }

        if (length < 0) throw new FormatException("bad header: " + header);

        byte[] body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await input.ReadAsync(body.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }

        return Encoding.UTF8.GetString(body);
    }

    private static Task WriteFrameAsync(object message, MemPipeStream output, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(message, JsonOptions);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        byte[] headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
        return PipeWriteAsync(output, headerBytes, bodyBytes, ct);
    }

    private static async Task PipeWriteAsync(MemPipeStream output, byte[] header, byte[] body, CancellationToken ct)
    {
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(body, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _serverToClient.End();
        _clientToServer.End();
        await Client.DisposeAsync().ConfigureAwait(false);
    }
}
