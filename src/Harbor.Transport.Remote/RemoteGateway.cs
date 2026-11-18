using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        _app = builder.Build();
        ILogger<RemoteGateway> logger = _app.Services.GetRequiredService<ILogger<RemoteGateway>>();

        // Single-endpoint gateway: keep-alive and buffering are configured
        // directly on the WebSocket middleware; no service registration is
        // needed for this shape.
        _app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });
        _app.Map("/ws", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                // No secret and no payload in the line, just why the upgrade
                // was refused.
                RemoteGatewayLog.NonWebSocketRequest(logger);
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!ctx.Request.Headers.TryGetValue("X-PSK", out var psk) || !PsAuthHandler.Validate(psk, _psk))
            {
                // Never log the presented or expected PSK, only the fact that
                // a handshake was rejected.
                RemoteGatewayLog.AuthenticationRejected(logger);
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await RunWebSocketAsync(ws, logger, ct).ConfigureAwait(false);
        });

        await _app.StartAsync(ct).ConfigureAwait(false);
        _runTask = Task.CompletedTask;
    }

    private async Task RunWebSocketAsync(WebSocket ws, ILogger<RemoteGateway> logger, CancellationToken ct)
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host shutdown is the expected exit, not a transport failure.
                break;
            }
            catch (Exception ex)
            {
                // A dropped connection used to end here with no trace at all.
                RemoteGatewayLog.WebSocketReceiveFailed(logger, ex);
                break;
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_app is null) return;
        await _app.StopAsync(ct).ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }
}

/// <summary>
///     SG: BCL <c>[LoggerMessage]</c> delegates for <see cref="RemoteGateway" />.
///     All messages are connection-level; no PSK or payload ever reaches them.
/// </summary>
internal static partial class RemoteGatewayLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Rejected non-WebSocket request to /ws")]
    public static partial void NonWebSocketRequest(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Rejected /ws handshake with an invalid PSK")]
    public static partial void AuthenticationRejected(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Remote UI WebSocket receive failed, closing the connection")]
    public static partial void WebSocketReceiveFailed(ILogger logger, Exception ex);
}
