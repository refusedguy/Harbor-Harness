using System.Text.Json;

namespace Harbor.Tools.Mcp;

/// <summary>
/// Internal JSON-RPC demultiplexer for the MCP client capabilities of #2661
/// (slice 1: handshake + roots). One pump owns one frame stream: it sends the
/// client <c>initialize</c> (declaring <c>roots.listChanged</c> and
/// <c>elicitation</c>), follows it with <c>notifications/initialized</c>, then
/// answers server-initiated <c>roots/list</c> requests while still returning
/// the addressed response to the caller. A working-directory change emits
/// <c>notifications/roots/list_changed</c> before the next request.
///
/// Everything here is internal: the public <c>IMcpRegistry</c> surface is not
/// touched, and AOT stays intact (no reflection, no dynamic code, all JSON
/// built through <see cref="McpJsonRpc"/>'s source-generated JsonElement
/// builders and parsed with <c>Utf8JsonReader</c>-backed JsonElement).
/// </summary>
internal sealed class McpClientPump
{
    private readonly McpJsonRpcTransport _transport;
    private readonly Func<string> _rootProvider;
    private string? _lastRoot;

    /// <summary>Create a pump over one MCP frame stream.</summary>
    /// <param name="transport">The framed stdio transport this pump reads and writes.</param>
    /// <param name="rootProvider">
    /// Supplies the current root directory. Defaults to the process working
    /// directory; injectable so tests can simulate a directory change without
    /// mutating global process state.
    /// </param>
    public McpClientPump(McpJsonRpcTransport transport, Func<string>? rootProvider = null)
    {
        _transport = transport;
        _rootProvider = rootProvider ?? (static () => Environment.CurrentDirectory);
    }

    /// <summary>
    /// Send <c>initialize</c>, pump until its response arrives (answering any
    /// server-initiated request in between), then acknowledge with
    /// <c>notifications/initialized</c>.
    /// </summary>
    public async Task<JsonElement?> InitializeAsync(int id, CancellationToken ct = default)
    {
        await EmitRootsChangedIfMovedAsync(ct).ConfigureAwait(false);
        JsonElement request = McpJsonRpc.BuildRequest(id, "initialize", McpJsonRpc.BuildInitializeParams());
        await _transport.WriteAsync(request, ct).ConfigureAwait(false);

        JsonElement? response = await PumpUntilResponseAsync(id, ct).ConfigureAwait(false);
        await _transport.WriteAsync(McpJsonRpc.BuildNotification("notifications/initialized"), ct)
            .ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Send a method request (emitting <c>notifications/roots/list_changed</c>
    /// first when the root moved) and return only the response carrying
    /// <paramref name="id"/>.
    /// </summary>
    public async Task<JsonElement?> SendAsync(int id, string method, JsonElement args, CancellationToken ct = default)
    {
        await EmitRootsChangedIfMovedAsync(ct).ConfigureAwait(false);
        JsonElement request = McpJsonRpc.BuildRequest(id, method, args);
        await _transport.WriteAsync(request, ct).ConfigureAwait(false);
        return await PumpUntilResponseAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read frames until the one addressed to <paramref name="expectedId"/>.
    /// Server-initiated requests (method + id) are answered in place; method
    /// notifications without an id are already skipped by the transport, and
    /// responses to other ids are ignored so a stray frame cannot be mistaken
    /// for the answer the caller is waiting on.
    /// </summary>
    private async Task<JsonElement?> PumpUntilResponseAsync(int expectedId, CancellationToken ct)
    {
        while (true)
        {
            JsonElement? frame = await _transport.ReadAsync(ct).ConfigureAwait(false);
            if (frame is null) return null;

            JsonElement f = frame.Value;
            if (f.ValueKind != JsonValueKind.Object) continue;

            bool hasId = f.TryGetProperty("id", out JsonElement idEl);
            bool hasMethod = f.TryGetProperty("method", out JsonElement methodEl);

            if (hasId && hasMethod && methodEl.ValueKind == JsonValueKind.String)
            {
                if (string.Equals(methodEl.GetString(), "roots/list", StringComparison.Ordinal))
                {
                    JsonElement result = McpJsonRpc.BuildRootsListResult(_rootProvider());
                    await _transport.WriteAsync(McpJsonRpc.BuildResponse(idEl, result), ct).ConfigureAwait(false);
                }

                continue;
            }

            if (hasId && (f.TryGetProperty("result", out _) || f.TryGetProperty("error", out _)))
            {
                if (IdEquals(idEl, expectedId)) return f;
                continue;
            }
        }
    }

    private async Task EmitRootsChangedIfMovedAsync(CancellationToken ct)
    {
        string root = _rootProvider();
        if (_lastRoot is not null && !string.Equals(root, _lastRoot, StringComparison.Ordinal))
        {
            await _transport.WriteAsync(McpJsonRpc.BuildNotification("notifications/roots/list_changed"), ct)
                .ConfigureAwait(false);
        }

        _lastRoot = root;
    }

    private static bool IdEquals(JsonElement id, int expected)
    {
        if (id.ValueKind == JsonValueKind.Number)
            return id.TryGetInt32(out int n) && n == expected;

        // A string id never matches a numeric request id we emitted; keeping
        // this explicit avoids an exception on GetInt32 for a server that ids
        // its own frames as strings.
        return false;
    }
}
