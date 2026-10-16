using System.Net.WebSockets;
using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Harbor.Transport.Remote;

/// <summary>
///     Proxy (structural) over a remote UI gateway: the single access-control
///     point for the underlying <see cref="ClientWebSocket" /> (#1522).
///     State guard + PSK gate live here, inside the surrogate — callers never
///     touch the socket directly.
/// </summary>
public sealed class RemoteClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly string _psk;
    private int _disposed;

    public RemoteClient(string psk)
    {
        if (string.IsNullOrWhiteSpace(psk))
            throw new ArgumentException("PSK must not be empty.", nameof(psk));
        _psk = psk;
        _ws = new ClientWebSocket();
    }

    /// <summary>True only while the socket is open and not disposed.</summary>
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _ws.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _ws.Options.SetRequestHeader("X-PSK", _psk);
        await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
    }

    public async Task<Result> SendAsync(UiTransportPacket packet, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Result.Failure("RemoteClient is disposed.");
        if (_ws.State != WebSocketState.Open)
            return Result.Failure("RemoteClient is not connected. Call ConnectAsync first.");
        // #177 (safe half): source-generated type info serializes straight to
        // UTF-8 bytes — no reflection lookup, no intermediate string, AOT-safe.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet, RemoteJsonContext.Default.UiTransportPacket);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ws.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
        _ws.Dispose();
    }
}
