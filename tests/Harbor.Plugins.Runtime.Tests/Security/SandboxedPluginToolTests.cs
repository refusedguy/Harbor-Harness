using System.Collections.Concurrent;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests.Security;

/// <summary>
///     Execution sandbox contract (<see cref="SandboxedPluginTool" />): every
///     plugin-contributed tool call is bounded by a wall-clock timeout and an
///     allocation budget; blocks surface as error <see cref="ToolResult" />s plus
///     <see cref="PluginBlockedEvent" /> and a deny audit line, while ordinary
///     capability use is audited as allow.
/// </summary>
public sealed class SandboxedPluginToolTests
{
    private static readonly ToolContext Ctx = new(
        SessionId: "session-1",
        MessageId: "msg-1",
        CallId: "call-1",
        Agent: "code",
        Abort: CancellationToken.None,
        Messages: Array.Empty<AgentMessage>(),
        ReportProgress: (_, _) => Task.CompletedTask,
        Ask: (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        Services: null!);

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static SandboxedPluginTool Wrap(
        ITool inner,
        RecordingEventBus bus,
        RecordingAuditLog audit,
        TimeSpan? timeout = null,
        long memoryBudget = SandboxedPluginTool.DefaultMemoryBudgetBytes,
        IReadOnlySet<PluginCapability>? capabilities = null) =>
        new(
            inner,
            "google_search",
            bus,
            NullLogger.Instance,
            timeout,
            memoryBudget,
            capabilities,
            audit);

    [Test]
    public async Task Defaults_Timeout30s_MemoryBudget10MB()
    {
        await Assert.That(SandboxedPluginTool.DefaultTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(SandboxedPluginTool.DefaultMemoryBudgetBytes).IsEqualTo(10L * 1024 * 1024);
    }

    [Test]
    public async Task Execute_InnerSuccess_PassesThroughAndAuditsAllow()
    {
        var bus = new RecordingEventBus();
        var audit = new RecordingAuditLog();
        var inner = new FakeTool(Args("""{"path":"notes.txt"}"""), ToolResult.Success("ok"));
        var tool = Wrap(
            inner,
            bus,
            audit,
            capabilities: new HashSet<PluginCapability> { PluginCapability.ReadFiles });

        var result = await tool.ExecuteAsync(Args("""{"path":"notes.txt"}"""), Ctx);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(bus.Events).IsEmpty();
        await Assert.That(audit.Entries).Count().IsEqualTo(1);
        var entry = audit.Entries[0];
        await Assert.That(entry.Capability).IsEqualTo(PluginCapability.ReadFiles);
        await Assert.That(entry.Target).IsEqualTo("notes.txt");
        await Assert.That(entry.Result).IsEqualTo("allow");
    }

    [Test]
    public async Task Execute_InnerHonorsBudgetCancellation_BlocksAsTimeout()
    {
        var bus = new RecordingEventBus();
        var audit = new RecordingAuditLog();
        var inner = new FakeTool(
            Args("{}"),
            ToolResult.Success("never"),
            delay: TimeSpan.FromSeconds(5));
        var tool = Wrap(
            inner,
            bus,
            audit,
            timeout: TimeSpan.FromMilliseconds(100),
            capabilities: new HashSet<PluginCapability> { PluginCapability.ReadFiles });

        var result = await tool.ExecuteAsync(Args("{}"), Ctx);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("[sandbox:timeout]");
        await Assert.That(bus.Of<PluginBlockedEvent>()).Count().IsEqualTo(1);
        await Assert.That(bus.Of<PluginBlockedEvent>()[0].Reason).IsEqualTo("timeout");
        await Assert.That(audit.Entries.Single(e => e.Result == "deny").Detail).Contains("timeout");
    }

    [Test]
    public async Task Execute_InnerIgnoresToken_WaitAsyncStillKills()
    {
        var bus = new RecordingEventBus();
        var audit = new RecordingAuditLog();
        var inner = new FakeTool(
            Args("{}"),
            ToolResult.Success("never"),
            delay: TimeSpan.FromSeconds(5),
            ignoresToken: true);
        var tool = Wrap(
            inner,
            bus,
            audit,
            timeout: TimeSpan.FromMilliseconds(100));

        var result = await tool.ExecuteAsync(Args("{}"), Ctx);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("[sandbox:timeout]");
        await Assert.That(bus.Of<PluginBlockedEvent>()).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Execute_MemoryOverBudget_BlocksAsMemory()
    {
        var bus = new RecordingEventBus();
        var audit = new RecordingAuditLog();
        var inner = new FakeTool(Args("{}"), ToolResult.Success("ok"), allocateBytes: 64 * 1024);
        var tool = Wrap(inner, bus, audit, memoryBudget: 1);

        var result = await tool.ExecuteAsync(Args("{}"), Ctx);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("[sandbox:memory]");
        await Assert.That(bus.Of<PluginBlockedEvent>()).Count().IsEqualTo(1);
        await Assert.That(bus.Of<PluginBlockedEvent>()[0].Reason).IsEqualTo("memory");
    }

    [Test]
    public async Task Execute_AgentLoopCancelled_PropagatesWithoutBlockEvent()
    {
        var bus = new RecordingEventBus();
        var audit = new RecordingAuditLog();
        var inner = new FakeTool(Args("{}"), ToolResult.Success("x"), delay: TimeSpan.FromSeconds(5));
        var tool = Wrap(
            inner,
            bus,
            audit,
            timeout: TimeSpan.FromSeconds(30),
            capabilities: new HashSet<PluginCapability> { PluginCapability.ReadFiles });

        using var cancelledCts = new CancellationTokenSource();
        await cancelledCts.CancelAsync();
        var cancelledCtx = Ctx with { Abort = cancelledCts.Token };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tool.ExecuteAsync(Args("{}"), cancelledCtx, cancelledCts.Token));

        await Assert.That(bus.Events).IsEmpty();
        await Assert.That(audit.Entries).IsEmpty();
    }

    [Test]
    public async Task Execute_InnerErrorResult_PassesThroughWithoutAllowAudit()
    {
        var bus = new RecordingEventBus();
        var audit = new RecordingAuditLog();
        var inner = new FakeTool(Args("{}"), ToolResult.Error("boom"));
        var tool = Wrap(
            inner,
            bus,
            audit,
            capabilities: new HashSet<PluginCapability> { PluginCapability.ReadFiles });

        var result = await tool.ExecuteAsync(Args("{}"), Ctx);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(bus.Of<PluginBlockedEvent>()).IsEmpty();
        await Assert.That(audit.Entries).IsEmpty();
    }

    private sealed class FakeTool : ITool
    {
        private readonly ToolResult _result;
        private readonly TimeSpan? _delay;
        private readonly bool _ignoresToken;
        private readonly long _allocateBytes;

        public FakeTool(
            JsonElement schemaArgs,
            ToolResult result,
            TimeSpan? delay = null,
            bool ignoresToken = false,
            long allocateBytes = 0)
        {
            _result = result;
            _delay = delay;
            _ignoresToken = ignoresToken;
            _allocateBytes = allocateBytes;
            if (allocateBytes > 0)
                _ = new byte[allocateBytes];
        }

        public ToolName Name => ToolName.Create("google_search");
        public string DisplayName => "Google Search";
        public string Description => "Fake plugin tool for sandbox tests";
        public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();
        public JsonDocument ParameterSchema => JsonDocument.Parse("{}");

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public async Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            if (_delay is { } delay)
            {
                if (_ignoresToken)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            return _result;
        }
    }
}

/// <summary>In-memory <see cref="IPluginAuditLog" /> capturing entries for assertions.</summary>
public sealed class RecordingAuditLog : IPluginAuditLog
{
    private readonly ConcurrentQueue<RecordingAuditEntry> _entries = new();

    /// <summary>Captured entries, in write order.</summary>
    public IReadOnlyList<RecordingAuditEntry> Entries => _entries.ToArray();

    public Task WriteAsync(
        string pluginName,
        PluginCapability capability,
        string target,
        string result,
        string? detail = null,
        CancellationToken ct = default)
    {
        _entries.Enqueue(new RecordingAuditEntry(pluginName, capability, target, result, detail));
        return Task.CompletedTask;
    }
}

/// <summary>One captured audit record.</summary>
public sealed record RecordingAuditEntry(
    string PluginName,
    PluginCapability Capability,
    string Target,
    string Result,
    string? Detail);
