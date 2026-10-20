// PromptPipelineObservationTests.cs — #1852: the REPL's detached prompt task
// is OBSERVED, and the observation is the test's subject.
//
// WHAT THIS TEST IS
// -----------------
// `PromptPipeline.StartPromptRun` detaches the prompt run on purpose: the
// REPL frame loop must not wait for a model turn. The detached task used to
// be a bare `_ = RunPromptAsync(...)`, which parks a fault on the Task where
// only finalization can see it — and a REPL lives for hours, so a fault on
// the prompt path was reported NOWHERE.
//
// The fix routes the detach through the house fire-and-forget helper, which
// reads the task's exception and reports it through a fault sink at the
// detach site. The observable behaviour is therefore: a fault in the prompt
// run REACHES A LOG at the detach site.
//
// The control is the event id. `PromptPipelineLog.PromptFailed` (event 1) is
// the runner's own catch-all; `PromptPipelineLog.PromptTaskFaulted` (event 2)
// is the detach site's observer. Event 1 fires for every ordinary prompt
// failure; event 2 fires only for a fault that escaped the runner's own net.
// Before the fix, event 2 did not exist and nothing observed the task at all
// — so the assertion below fails on the pre-fix code, which is what makes it
// a control rather than a description.
//
// WHY THE FAULT IS PROVOKED THROUGH A BROKEN BRIDGE
// ----------------------------------------------
// The runner's catch-all handles the prompt failure and then REPORTS it on
// the timeline: `host.Bridge.AppendSystemLine(...)`. The stub host's bridge
// is not wired, so that report throws — and an exception thrown inside a
// catch block escapes it. The prompt task therefore faults, which is the
// situation the detach site's observer exists for. It is a realistic shape
// (the report path failing is exactly how a fault escapes a catch-all), and
// it is deterministic.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Git;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Repl;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Application.Configuration;
using Harbor.Hosting.Rendering;
using Harbor.Registries.Agents;
using Harbor.Registries.Providers;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #1852 — the REPL's detached prompt task is observed at the detach
///     site.
/// </summary>
public sealed class PromptPipelineObservationTests
{
    /// <summary>
    ///     The detach site's observer: <c>PromptPipelineLog.PromptTaskFaulted</c>.
    /// </summary>
    /// <remarks>
    ///     The runner's own catch-all is event 1; this one is event 2, and it
    ///     exists only because the detach site observes the task.
    /// </remarks>
    private const int PromptTaskFaultedEventId = 2;

    [Test]
    public async Task A_Fault_In_The_Detached_Prompt_Task_Reaches_The_Detach_Site_Observer()
    {
        var logger = new CapturingLogger();
        var host = new StubHost(new FaultingAgent());

        using var pipeline = new PromptPipeline(
            host,
            new ReplCommandCatalog(),
            logger,
            null,
            new Lazy<LegacySlashRunner>(static () => null!));

        host.Composer.Buffer.InsertText("hello");

        await pipeline.SubmitAsync(CancellationToken.None);

        // The prompt is detached BY DESIGN, so the observer's continuation
        // runs on the thread pool. Wait for it rather than assume it has
        // already run.
        for (int attempt = 0; attempt < 200 && !logger.HasEvent(PromptTaskFaultedEventId); attempt++)
        {
            await Task.Delay(10);
        }

        await Assert.That(logger.HasEvent(PromptTaskFaultedEventId)).IsTrue()
            .Because(
                "the prompt task is detached on purpose — the REPL frame loop must not wait for a model "
                + "turn — and observed on purpose: the house fire-and-forget helper reads the task's "
                + "exception and the detach site reports it. A fault nobody observes is reported nowhere: "
                + ".NET parks the exception on the Task and only raises UnobservedTaskException at "
                + "finalization, which for a REPL that lives for hours is never. Logged entries: "
                + string.Join(" | ", logger.Describe()));

        await Assert.That(logger.HasEvent(1)).IsTrue()
            .Because(
                "the runner's own catch-all must have fired first — the fault has to ESCAPE that net "
                + "before the detach site's observer has anything to report. If event 1 is missing, the "
                + "test is not exercising the path it claims to exercise. Logged entries: "
                + string.Join(" | ", logger.Describe()));
    }

    /// <summary>
    ///     A logger that keeps what it was told, so the test can assert on
    ///     the EVENT rather than on a message string.
    /// </summary>
    /// <remarks>
    ///     The event id is the assertion's subject: the runner's catch-all
    ///     and the detach site's observer are two different events, and a
    ///     test that matched on the message text would pass on either.
    /// </remarks>
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<(EventId Id, LogLevel Level, string Message)> _entries = [];

        /// <summary>Whether an event with this id was logged.</summary>
        public bool HasEvent(int eventId)
        {
            lock (_entries)
            {
                return _entries.Any(static e => e.Id.Id == eventId);
            }
        }

        /// <summary>The entries, as printable text.</summary>
        public string Describe()
        {
            lock (_entries)
            {
                return string.Join(" | ", _entries.Select(static e => $"[{e.Id.Id}] {e.Message}"));
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((eventId, logLevel, formatter(state, exception)));
            }
        }
    }

    /// <summary>
    ///     An agent whose prompt FAILS. The failure is what the detach
    ///     site's observer has to see.
    /// </summary>
    private sealed class FaultingAgent : IAgent
    {
        private readonly CancellationTokenSource _abortSource = new();

        public Maybe<AgentState> State => Maybe<AgentState>.None;

        public CancellationToken AbortToken => _abortSource.Token;

        public void RequestAbort() => _abortSource.Cancel();

        public Task<Result> PromptAsync(string text, CancellationToken ct = default) =>
            throw new InvalidOperationException("the provider is on fire");

        public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default) =>
            throw new InvalidOperationException("the provider is on fire");

        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void ResetAbortSource()
        {
        }

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => new Nop();

        public void Initialize(Session session, AgentDefinition agent)
        {
        }

        public void Steer(AgentMessage message)
        {
        }

        public void Dispose() => _abortSource.Dispose();

        private sealed class Nop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    ///     The REPL host, wired with the pieces the prompt path touches and
    ///     nothing else. The BRIDGE is deliberately not wired: the runner
    ///     reports a prompt failure on the timeline, and that report is what
    ///     makes the fault escape the runner's own catch-all.
    /// </summary>
    private sealed class StubHost(IAgent agent) : IReplHost
    {
        private readonly ComposerController _composer = new();
        private readonly VirtualizedChatTimeline _timeline = new();

        public IAgent Agent { get; } = agent;
        public Session SessionModel { get; set; } = null!;
        public ChatScreenBridge Bridge => null!;
        public UiStore Store => null!;
        public CommandPaletteView Palette => null!;
        public StatusViewModel Status { get; } = new();
        public ChatScreen Screen => null!;
        public SelectionEngine Selection => null!;
        public VirtualizedChatTimeline Timeline => _timeline;
        public ComposerController Composer => _composer;
        public IConfigStore ConfigStore => null!;
        public IProviderRegistry ProviderRegistry => null!;
        public IAgentRegistry AgentRegistry => null!;
        public AuthStore AuthStore => null!;
        public ISessionStore? SessionStore => null;
        public IRendererPipeline? RendererPipeline => null;
        public Harbor.Hosting.PluginReloadService? PluginReload => null;
        public IProviderHealthCheck? HealthCheck => null;
        public IGitQuery? Git => null;
        public IPanelRegistry? PanelRegistry => null;
        public ImageAttachmentStash? Attachments => null;

        public void WakeUp()
        {
        }

        public void OpenSlashPalette()
        {
        }

        public void ToggleVimMode()
        {
        }

        public void ScrollTimelineToEnd()
        {
        }

        public void RequestQuit(int exitCode)
        {
        }

        public Task SwitchToSessionAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;

        public Task ExecutePaletteItemAsync(CommandItem item, CancellationToken ct) => Task.CompletedTask;

        public Task ExecuteInfoAsync(string text, CancellationToken ct) => Task.CompletedTask;

        public Task SyncSessionsToStoreAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<int> ResolveContextWindowAsync(string providerId, string modelId, CancellationToken ct) =>
            Task.FromResult(0);
    }
}
