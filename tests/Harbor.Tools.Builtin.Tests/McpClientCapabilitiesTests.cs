using System.Text;
using System.Text.Json;
using Harbor.Tools.Mcp;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #2661 slice 1 — MCP client capabilities. These tests pin the internal
///     handshake/roots machinery without touching the live registry call path:
///     the initialize envelope declares roots.listChanged + elicitation, the
///     initialized frame is a method-only notification, the pump answers a
///     server-initiated roots/list in the middle of a call and still returns
///     the addressed response, and a working-directory change emits
///     notifications/roots/list_changed before the next request.
/// </summary>
[NotInParallel("process-spawn")]
public class McpClientCapabilitiesTests
{
    [Test]
    public async Task InitializeParams_DeclaresRootsAndElicitation()
    {
        JsonElement p = McpJsonRpc.BuildInitializeParams();
        JsonElement caps = p.GetProperty("capabilities");

        await Assert.That(p.GetProperty("protocolVersion").GetString()).IsEqualTo("2024-11-05");
        await Assert.That(caps.GetProperty("roots").GetProperty("listChanged").GetBoolean()).IsTrue();
        await Assert.That(caps.GetProperty("elicitation").ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(p.GetProperty("clientInfo").GetProperty("name").GetString()).IsEqualTo("harbor");
    }

    [Test]
    public async Task InitializedNotification_HasMethodButNoId()
    {
        JsonElement n = McpJsonRpc.BuildNotification("notifications/initialized");

        await Assert.That(n.GetProperty("jsonrpc").GetString()).IsEqualTo("2.0");
        await Assert.That(n.GetProperty("method").GetString()).IsEqualTo("notifications/initialized");
        await Assert.That(n.TryGetProperty("id", out _)).IsFalse();
    }

    [Test]
    public async Task RootsListResult_UsesFileUri()
    {
        JsonElement r = McpJsonRpc.BuildRootsListResult(Path.GetTempPath());
        string uri = r.GetProperty("roots")[0].GetProperty("uri").GetString()!;

        await Assert.That(uri).StartsWith("file://");
    }

    /// <summary>
    ///     The acceptance shape for the demux: a roots/list request arrives
    ///     between the caller's request and its answer. The pump answers the
    ///     server request and still hands id=7 back to the caller.
    /// </summary>
    [Test]
    public async Task Pump_AnswersRootsList_AndStillReturnsAddressedResponse()
    {
        const string frames =
            "{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"roots/list\",\"params\":{}}\n"
            + "{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{\"tools\":[]}}\n";
        string root = Path.GetTempPath();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(frames));
        using var output = new MemoryStream();
        await using var transport = new McpJsonRpcTransport(input, output);
        var pump = new McpClientPump(transport, () => root);

        JsonElement? response = await pump.SendAsync(7, "tools/list", McpJsonRpc.BuildToolsListParams(null));

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.Value.GetProperty("id").GetInt32()).IsEqualTo(7);
        string written = Encoding.UTF8.GetString(output.ToArray());
        await Assert.That(written).Contains("\"method\":\"tools/list\""); // the caller's request
        await Assert.That(written).Contains("\"id\":99"); // the answer echoed to the server
        await Assert.That(written).Contains("file://"); // a file root in that answer
    }

    [Test]
    public async Task Pump_EmitsRootsChanged_WhenRootMoves()
    {
        string current = Path.GetTempPath();
        const string frames =
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{}}\n"
            + "{\"jsonrpc\":\"2.0\",\"id\":8,\"result\":{}}\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(frames));
        using var output = new MemoryStream();
        await using var transport = new McpJsonRpcTransport(input, output);
        var pump = new McpClientPump(transport, () => current);

        await pump.SendAsync(7, "initialize", McpJsonRpc.BuildInitializeParams());
        current = Path.Combine(Path.GetTempPath(), "moved-root");
        await pump.SendAsync(8, "tools/list", McpJsonRpc.BuildToolsListParams(null));

        string written = Encoding.UTF8.GetString(output.ToArray());
        await Assert.That(written).Contains("notifications/roots/list_changed");
    }

    [Test]
    public async Task Initialize_SendsNotificationAfterResponse()
    {
        const string frames = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2024-11-05\"}}\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(frames));
        using var output = new MemoryStream();
        await using var transport = new McpJsonRpcTransport(input, output);
        var pump = new McpClientPump(transport);

        JsonElement? response = await pump.InitializeAsync(1);

        await Assert.That(response).IsNotNull();
        string written = Encoding.UTF8.GetString(output.ToArray());
        int initAt = written.IndexOf("\"method\":\"initialize\"", StringComparison.Ordinal);
        int ackAt = written.IndexOf("notifications/initialized", StringComparison.Ordinal);
        await Assert.That(initAt).IsGreaterThanOrEqualTo(0);
        await Assert.That(ackAt).IsGreaterThan(initAt);
    }
}
