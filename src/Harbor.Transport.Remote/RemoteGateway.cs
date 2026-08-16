using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebSockets;
using System.Net.WebSockets;

namespace Harbor.Transport.Remote;

public sealed class RemoteGateway
{
    private readonly string _psk;
    private WebApplication? _app;
    private Task? _runTask;

    public RemoteGateway(string psk)
    {
        _psk = psk;
    }

    public async Task StartAsync(int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddWebSockets(options = { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/ws", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!ctx.Request.Headers.TryGetValue("X-PSK", out var psk) || !PsAuthHandler.Validate(psk, _psk))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await RunWebSocketAsync(ws, ct).ConfigureAwait(false);
        });

        await _app.StartAsync(ct).ConfigureAwait(false);
        _runTask = Task.CompletedTask;
    }

    private async Task RunWebSocketAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
            catch
            {
                break;
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_app is null) return;
        await _app.StopAsync(ct).ConfigureAwait(false);
        _app.Dispose();
        _app = null;
    }
}
