using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
/// Wire-allocation slice (#180): the MCP request/params builders emit
/// byte-identical envelopes without string-interpolated JSON, the SSE
/// response filter stays behavior-identical while keeping the id-mismatch
/// path DOM-free, and tools/call without arguments sends <c>{}</c> instead
/// of throwing on an unusable <c>JsonElement</c>.
/// </summary>
public class McpWireAllocTests
{
    // ---------- McpSse.TryParseResponse ----------

    [Test]
    public async Task SseParser_MatchingId_ReturnsDocument()
    {
        using JsonDocument? doc = McpSse.TryParseResponse(
            """{"jsonrpc":"2.0","id":7,"result":{"ok":true}}""", 7);

        await Assert.That(doc).IsNotNull();
        await Assert.That(doc!.RootElement.GetProperty("id").GetInt32()).IsEqualTo(7);
    }

    [Test]
    public async Task SseParser_NullFilter_ReturnsAnyDocument()
    {
        using JsonDocument? doc = McpSse.TryParseResponse("[1,2]", null);

        await Assert.That(doc).IsNotNull();
        await Assert.That(doc!.RootElement.GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task SseParser_WhitespaceFraming_StillMatches()
    {
        using JsonDocument? doc = McpSse.TryParseResponse(
            """  {"jsonrpc":"2.0","id":7,"result":{}}  """, 7);

        await Assert.That(doc).IsNotNull();
    }

    [Test]
    [Arguments("""{"jsonrpc":"2.0","id":8,"result":{}}""", 7)]
    [Arguments("""{"jsonrpc":"2.0","id":"7","result":{}}""", 7)]
    [Arguments("""{"jsonrpc":"2.0","result":{}}""", 7)]
    [Arguments("""[1,2]""", 7)]
    [Arguments("this is not json", 7)]
    [Arguments("", 7)]
    [Arguments("""{"jsonrpc":"2.0","id":7,"result":{}} trailing""", 7)]
    public async Task SseParser_NonMatchingFrame_ReturnsNull(string data, int expectedId)
    {
        await Assert.That(McpSse.TryParseResponse(data, expectedId)).IsNull();
    }

    [Test]
    public async Task SseParser_MismatchPath_StaysDomFree()
    {
        const string frame = """{"jsonrpc":"2.0","id":8,"result":{"tools":[{"name":"a","description":"b","inputSchema":{"type":"object"}}]}}""";
        for (int i = 0; i < 50; i++)
            _ = McpSse.TryParseResponse(frame, 7); // warmup: pool + tier-up

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
            _ = McpSse.TryParseResponse(frame, 7);
        long after = GC.GetAllocatedBytesForCurrentThread();

        // No DOM is ever built on the mismatch path — pooled encode + span
        // scan only. Generous bound (~164 B/iter); the old parse + dispose
        // cost ~1 KB+ per frame.
        await Assert.That(after - before).IsLessThanOrEqualTo(200 * 164);
    }

    // ---------- McpJsonRpc builders ----------

    [Test]
    public async Task RequestBuilder_EmitsValidEnvelope_WithEscapedMethod()
    {
        using var paramsDoc = JsonDocument.Parse("""{"x":1}""");
        using JsonDocument request = McpJsonRpc.BuildRequest(7, "too\"ls/call", paramsDoc.RootElement);
        JsonElement root = request.RootElement;

        await Assert.That(root.GetProperty("jsonrpc").GetString()).IsEqualTo("2.0");
        await Assert.That(root.GetProperty("id").GetInt32()).IsEqualTo(7);
        await Assert.That(root.GetProperty("method").GetString()).IsEqualTo("too\"ls/call");
        await Assert.That(root.GetProperty("params").GetProperty("x").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task ToolCallParams_MissingArguments_EmitsEmptyObject()
    {
        using JsonDocument paramsDoc = McpJsonRpc.BuildToolCallParams("t", default);

        await Assert.That(paramsDoc.RootElement.GetRawText()).IsEqualTo("""{"name":"t","arguments":{}}""");
    }

    [Test]
    public async Task PromptGetParams_EscapesName_Verbatim()
    {
        const string name = "we\"ird\nname";
        using JsonDocument paramsDoc = McpJsonRpc.BuildPromptGetParams(name, default);

        await Assert.That(paramsDoc.RootElement.GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(paramsDoc.RootElement.GetProperty("arguments").ValueKind).IsEqualTo(JsonValueKind.Object);
    }

    [Test]
    public async Task ResourceReadParams_EscapesUri_Verbatim()
    {
        const string uri = "file:///a\"b.md";
        using JsonDocument paramsDoc = McpJsonRpc.BuildResourceReadParams(uri);

        await Assert.That(paramsDoc.RootElement.GetProperty("uri").GetString()).IsEqualTo(uri);
    }

    [Test]
    public async Task RegisterClientBody_Shape()
    {
        using JsonDocument body = McpJsonRpc.BuildRegisterClientBody("https://app/cb", "a b");

        await Assert.That(body.RootElement.GetRawText()).IsEqualTo(
            """{"redirect_uris":["https://app/cb"],"grant_types":["authorization_code","refresh_token"],"scope":"a b"}""");
    }

    // ---------- Public tool surface (registry interop) ----------

    [Test]
    public async Task Adapter_WithoutArguments_SendsEmptyObject()
    {
        var registry = ScriptedMcpRegistry.Succeed("{}");
        var tool = new McpToolAdapter(registry, "srv", "tool");

        using var argsDoc = JsonDocument.Parse("{}");
        var result = await tool.ExecuteAsync(argsDoc.RootElement, CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(registry.LastMethod).IsEqualTo("tools/call");
        await Assert.That(registry.LastArgsJson).IsEqualTo("""{"name":"tool","arguments":{}}""");
    }

    [Test]
    public async Task PromptTool_ExecuteAsync_EscapesName()
    {
        var registry = ScriptedMcpRegistry.Succeed(
            """{"messages":[{"role":"user","content":{"type":"text","text":"hi"}}]}""");
        var tool = new McpPromptTool(registry, NullLogger<McpPromptTool>.Instance);

        using var argsDoc = JsonDocument.Parse("""{"server":"s","name":"we\"ird"}""");
        var result = await tool.ExecuteAsync(argsDoc.RootElement, CreateContext());

        await Assert.That(result.IsError).IsFalse();
        using var sent = JsonDocument.Parse(registry.LastArgsJson!);
        await Assert.That(sent.RootElement.GetProperty("name").GetString()).IsEqualTo("we\"ird");
    }

    [Test]
    public async Task ResourceTool_ExecuteAsync_EscapesUri()
    {
        var registry = ScriptedMcpRegistry.Succeed(
            """{"contents":[{"uri":"u","text":"t"}]}""");
        var tool = new McpResourceTool(registry, NullLogger<McpResourceTool>.Instance);

        using var argsDoc = JsonDocument.Parse("""{"server":"s","uri":"file:///a\"b.md"}""");
        var result = await tool.ExecuteAsync(argsDoc.RootElement, CreateContext());

        await Assert.That(result.IsError).IsFalse();
        using var sent = JsonDocument.Parse(registry.LastArgsJson!);
        await Assert.That(sent.RootElement.GetProperty("uri").GetString()).IsEqualTo("file:///a\"b.md");
    }

    private static ToolContext CreateContext() => new(
        "test-session",
        "test-message",
        "test-call",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);
}
