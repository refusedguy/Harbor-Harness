using System.Net.WebSockets;
using System.Text.Json;

namespace Harbor.Transport.Remote;

public sealed class RemoteClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly string _psk;

    public RemoteClient(string psk)
    {
        _psk = psk;
        _ws = new ClientWebSocket();
    }

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        _ws.Options.SetRequestHeader("X-PSK", _psk);
        await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
    }

    public async Task SendAsync(UiTransportPacket packet, CancellationToken ct)
    {
        // #177 (safe half): source-generated type info serializes straight to
        // UTF-8 bytes — no reflection lookup, no intermediate string, AOT-safe.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet, RemoteJsonContext.Default.UiTransportPacket);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
        _ws.Dispose();
    }
}
