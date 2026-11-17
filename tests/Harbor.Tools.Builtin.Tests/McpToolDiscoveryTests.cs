using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Registries.Agents;
using Harbor.Registries.Tools;
using Harbor.TestKit;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #2227: a server's <c>tools/list</c> answer is the registration source for
///     real <see cref="ITool"/> instances, not a payload the model has to decode
///     through the generic <c>mcp</c> bridge.
/// </summary>
public class McpToolDiscoveryTests
{
    [Test]
    public async Task RegisterAsync_AddsEveryAdvertisedTool_AndGrantsItsPermission()
    {
        var mcp = new FakeMcpRegistry(["srv"]);
        mcp.Handler = (_, method, _) => method == "tools/list"
            ? Result.Success("""{"result":{"tools":[{"name":"echo","description":"Echo"},{"name":"add","description":"Add"}]}}""")
            : Result.Failure<string>("unexpected method");
        var tools = new ToolRegistry();
        var agents = new AgentRegistry();
        agents.Register(AgentDefinition.CodeDefault("model", "provider"));

        int added = await McpToolDiscovery.RegisterAsync(
            mcp, tools, agents, NullLoggerFactory.Instance, NullLogger.Instance);

        await Assert.That(added).IsEqualTo(2);
        var names = tools.GetAllTools().Select(t => t.Name.Value).ToArray();
        await Assert.That(names).Contains("mcp_srv_echo");
        await Assert.That(names).Contains("mcp_srv_add");

        ITool adapter = tools.GetTool(ToolName.Create("mcp_srv_echo")).Value;
        await Assert.That(adapter.SafetyProfile.ArgKind).IsEqualTo(ToolArgKind.Opaque);

        var agent = agents.GetAgent(AgentName.Create("code")).Value;
        await Assert.That(agent.Permission.Evaluate("mcp_srv_echo", "*")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(agent.Permission.Evaluate("mcp_srv_add", "*")).IsEqualTo(PermissionAction.Allow);

        // The agent calls the adapter exactly like a builtin: same ITool, same
        // tool context, and the registry forwards to tools/call on its server.
        mcp.Handler = (_, method, args) =>
        {
            mcp.LastMethod = method;
            mcp.LastArgsJson = args.GetRawText();
            return Result.Success("""{"result":{"content":[]}}""");
        };
        using var callArgs = JsonDocument.Parse("""{"arguments":{"text":"hello"}}""");
        ToolResult call = await adapter.ExecuteAsync(callArgs.RootElement, TestToolContext.Create());

        await Assert.That(call.IsError).IsFalse();
        await Assert.That(mcp.LastMethod).IsEqualTo("tools/call");
        await Assert.That(mcp.LastArgsJson).IsEqualTo("""{"name":"echo","arguments":{"text":"hello"}}""");
    }

    [Test]
    public async Task RegisterAsync_FollowsNextCursor_SoALaterPageIsNotSilentlyDropped()
    {
        // #2228: tools/list is paginated. A single un-cursored call would see
        // page one only, and every tool after it would be absent from the prompt
        // and from the dispatcher.
        var mcp = new FakeMcpRegistry(["big"]);
        mcp.Handler = (_, method, args) =>
        {
            mcp.LastMethod = method;
            string raw = args.GetRawText();
            mcp.RequestArgs.Add(raw);
            bool firstPage = raw == "{}";
            return Result.Success(firstPage
                ? """{"result":{"tools":[{"name":"one"},{"name":"two"}],"nextCursor":"2"}}"""
                : """{"result":{"tools":[{"name":"three"}]}}""");
        };
        var tools = new ToolRegistry();
        var agents = new AgentRegistry();
        agents.Register(AgentDefinition.CodeDefault("model", "provider"));

        int added = await McpToolDiscovery.RegisterAsync(
            mcp, tools, agents, NullLoggerFactory.Instance, NullLogger.Instance);

        await Assert.That(added).IsEqualTo(3);
        var names = tools.GetAllTools().Select(t => t.Name.Value).ToArray();
        await Assert.That(names).Contains("mcp_big_one");
        await Assert.That(names).Contains("mcp_big_two");
        await Assert.That(names).Contains("mcp_big_three");

        // The second request must carry the cursor the server named.
        await Assert.That(mcp.RequestArgs.Count).IsEqualTo(2);
        await Assert.That(mcp.RequestArgs[0]).IsEqualTo("{}");
        await Assert.That(mcp.RequestArgs[1]).IsEqualTo("""{"cursor":"2"}""");
    }

    [Test]
    public async Task RegisterAsync_RepeatedCursor_TerminatesInsteadOfLooping()
    {
        var mcp = new FakeMcpRegistry(["loop"]);
        mcp.Handler = (_, _, args) =>
        {
            mcp.RequestArgs.Add(args.GetRawText());
            return Result.Success("""{"result":{"tools":[{"name":"only"}],"nextCursor":"same"}}""");
        };
        var tools = new ToolRegistry();
        var agents = new AgentRegistry();
        agents.Register(AgentDefinition.CodeDefault("model", "provider"));

        int added = await McpToolDiscovery.RegisterAsync(
            mcp, tools, agents, NullLoggerFactory.Instance, NullLogger.Instance);

        await Assert.That(added).IsEqualTo(1);
        // First page, then one follow-up that names the cursor already seen.
        await Assert.That(mcp.RequestArgs.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RegisterAsync_ServerThatCannotList_ContributesNoTools()
    {
        var mcp = new FakeMcpRegistry(["gone"]) { Handler = (_, _, _) => Result.Failure<string>("server is gone") };
        var tools = new ToolRegistry();
        var agents = new AgentRegistry();
        agents.Register(AgentDefinition.CodeDefault("model", "provider"));

        int added = await McpToolDiscovery.RegisterAsync(
            mcp, tools, agents, NullLoggerFactory.Instance, NullLogger.Instance);

        await Assert.That(added).IsEqualTo(0);
        await Assert.That(tools.GetAllTools()).IsEmpty();
    }

    [Test]
    public async Task Adapter_CrashReturnsFailure_NextCallRespawnsTheProcess()
    {
        // #2227 / McpRegistry.cs:639-661: the adapter holds the server name, and
        // GetProcess respawns a dead process on the next invocation. First spawn
        // exits without a response; the second answers normally.
        string counter = Path.GetTempFileName();
        string script = """
        c="$1"
        n=$(cat "$c" 2>/dev/null || echo 0)
        n=$((n+1))
        echo "$n" > "$c"
        if [ "$n" -eq 1 ]; then exit 0; fi
        printf '%s\n' '{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"ok"}]}}'
        """;

        await using var registry = new McpRegistry(NullLogger<McpRegistry>.Instance, McpTransportResolver.Default);
        try
        {
            Result registered = registry.Register("flaky", new McpServerStartInfo
            {
                Command = "sh",
                Args = new[] { "-c", script, "_", counter }
            });
            await Assert.That(registered.IsSuccess).IsTrue();

            var adapter = new McpToolAdapter(registry, "flaky", "echo");
            using var args = JsonDocument.Parse("{}");

            ToolResult first = await adapter.ExecuteAsync(args.RootElement, TestToolContext.Create());
            await Assert.That(first.IsError).IsTrue();
            await Assert.That(first.Output).Contains("flaky");

            ToolResult second = await adapter.ExecuteAsync(args.RootElement, TestToolContext.Create());
            await Assert.That(second.IsError).IsFalse();
            await Assert.That(second.Output).Contains("ok");
        }
        finally
        {
            File.Delete(counter);
        }
    }

    /// <summary>Minimal registry with a configurable server list and invoke handler.</summary>
    private sealed class FakeMcpRegistry(IReadOnlyList<string> servers) : IMcpRegistry
    {
        internal Func<string, string, JsonElement, Result<string>> Handler { get; set; }
            = (_, _, _) => Result.Failure<string>("no handler");

        internal string? LastMethod { get; set; }
        internal string? LastArgsJson { get; set; }
        internal List<string> RequestArgs { get; } = new();

        public Result Register(string name, string stdioCommand) => Result.Success();
        public Result Unregister(string name) => Result.Success();
        public IReadOnlyList<string> GetServerNames() => servers;
        public IReadOnlyList<McpServerInstructions> GetInstructions() => Array.Empty<McpServerInstructions>();

        public Task<Result<string>> InvokeAsync(
            string server, string method, JsonElement args, CancellationToken cancellationToken = default)
            => Task.FromResult(Handler(server, method, args));
    }
}
