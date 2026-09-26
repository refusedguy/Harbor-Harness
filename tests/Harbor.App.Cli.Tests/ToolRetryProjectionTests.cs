using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Repl;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Application.Configuration;
using Harbor.Hosting.Rendering;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #76: the tool-dispatcher retry loop feeds the retry-countdown UI slot.
///     The pipeline mirrors attempt/max/backoff render-only (never triggers);
///     the frame loop pumps <see cref="PromptPipeline.TryGetRetryProjection"/>
///     into <c>SetProjectedRetry</c>.
/// </summary>
public class ToolRetryProjectionTests
{
    [Test]
    public async Task ToolRetryUpdate_FeedsProjection_AndEndClears()
    {
        var status = new StatusViewModel();
        using var pipeline = NewPipeline(status);

        pipeline.ObserveEvent(new ToolExecutionUpdateEvent(
            "tc1", "working…", RetryAttempt: 1, RetryMaxAttempts: 3, RetryBackoffSeconds: 0.2));

        await Assert.That(status.Retry).IsEqualTo("retry 1/3 in 1s");
        await Assert.That(pipeline.TryGetRetryProjection(out int attempt, out int max, out int remaining)).IsTrue();
        await Assert.That(attempt).IsEqualTo(1);
        await Assert.That(max).IsEqualTo(3);
        await Assert.That(remaining).IsGreaterThanOrEqualTo(0);

        pipeline.ObserveEvent(new ToolExecutionEndEvent("tc1", ToolResult.Success("recovered"), IsError: false));

        await Assert.That(status.Retry).IsNull();
        await Assert.That(pipeline.TryGetRetryProjection(out _, out _, out _)).IsFalse();
    }

    [Test]
    public async Task StreamTransientError_StillFeedsProjection()
    {
        // The pre-existing stream-retry mirror keeps its (attempt, max 3) shape
        // after the tool-retry fields were unified.
        var status = new StatusViewModel();
        using var pipeline = NewPipeline(status);

        pipeline.ObserveEvent(new MessageUpdateEvent(
            new ErrorEvent("overloaded", Kind: ProviderErrorKind.ServerError),
            AssistantMessage.Empty("s", "m")));

        await Assert.That(status.Retry).IsEqualTo("retry 1/3 in 1s");
        await Assert.That(pipeline.TryGetRetryProjection(out int attempt, out int max, out _)).IsTrue();
        await Assert.That(attempt).IsEqualTo(1);
        await Assert.That(max).IsEqualTo(3);
    }

    private static PromptPipeline NewPipeline(StatusViewModel status) =>
        new(new StubHost(status, new RunningAgent()), new ReplCommandCatalog(), NullLogger.Instance,
            null, new Lazy<LegacySlashRunner>(static () => null!));

    private sealed class RunningAgent : IAgent
    {
        public RunningAgent()
        {
            var def = new AgentDefinition(
                AgentName.Create("code"), "Code", "retry harness",
                "test-model", "test", PermissionRuleset.Default);
            State = AgentState.Idle("s", def) with { IsRunning = true };
        }

        public CancellationToken AbortToken => _abortSource.Token;
        public void RequestAbort() => _abortSource.Cancel();
        private readonly CancellationTokenSource _abortSource = new();
        public AgentState State { get; }
        public void Initialize(Session session, AgentDefinition agent) { }
        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => new Nop();
        public Task<Result> PromptAsync(string text, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ResetAbortSource() { }
        public void Steer(AgentMessage message) { }
        public void Dispose() => _abortSource.Dispose();

        private sealed class Nop : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class StubHost(StatusViewModel status, IAgent agent) : IReplHost
    {
        public IAgent Agent { get; } = agent;
        public Session SessionModel { get; set; } = null!;
        public ChatScreenBridge Bridge => null!;
        public UiStore Store => null!;
        public CommandPaletteView Palette => null!;
        public StatusViewModel Status { get; } = status;
        public ChatScreen Screen => null!;
        public SelectionEngine Selection => null!;
        public VirtualizedChatTimeline Timeline => null!;
        public ComposerController Composer => null!;
        public IConfigStore ConfigStore => null!;
        public IProviderRegistry ProviderRegistry => null!;
        public IAgentRegistry AgentRegistry => null!;
        public AuthStore AuthStore => null!;
        public ISessionStore? SessionStore => null;
        public IRendererPipeline? RendererPipeline => null;
        public void WakeUp() { }
        public void OpenSlashPalette() { }
        public void ToggleVimMode() { }
        public void ScrollTimelineToEnd() { }
        public void RequestQuit(int exitCode) { }
        public Task SwitchToSessionAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
        public Task ExecutePaletteItemAsync(CommandItem item, CancellationToken ct) => Task.CompletedTask;
        public Task ExecuteInfoAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public Task SyncSessionsToStoreAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<int> ResolveContextWindowAsync(string providerId, string modelId, CancellationToken ct) => Task.FromResult(0);
    }
}
