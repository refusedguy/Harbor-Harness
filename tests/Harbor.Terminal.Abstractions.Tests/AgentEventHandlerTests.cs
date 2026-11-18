using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions.Renderers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Harbor.TestKit;

namespace Harbor.Terminal.Abstractions.Tests;

/// <summary>
///     Visitor-registry contract for <see cref="BaseTuiRenderer"/> (issue #185):
///     handlers whose <see cref="IAgentEventHandler.CanHandle"/> matches receive
///     the event, the rest are skipped, and one failing handler never blocks the
///     others. Adding a new <see cref="AgentEvent"/> type must not require
///     touching this dispatch path.
/// </summary>
public class AgentEventHandlerRegistryTests
{
    private sealed class HandlerTestRenderer : BaseTuiRenderer
    {
        public HandlerTestRenderer() : this(NullLogger.Instance) { }

        public HandlerTestRenderer(ILogger logger) : base(logger) { }

        public CaptureRenderContext Capture { get; } = new();

        public override ITuiRenderContext Context => Capture;

        public void Add(IAgentEventHandler handler) => RegisterHandler(handler);

        public Task Dispatch(AgentEvent @event) => DispatchToHandlersAsync(@event);

        public Task<Result> Guarded(Action setup, string? detail = null) =>
            InitializeGuardedAsync(setup, detail);

        public override Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
            => Task.FromResult(Maybe.From(string.Empty));

        public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public override Task<Result> ClearAsync(CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }

    private sealed class StubHandler(Func<AgentEvent, bool> canHandle, Func<AgentEvent, Task>? onHandle = null)
        : IAgentEventHandler
    {
        public int Calls { get; private set; }

        public bool CanHandle(AgentEvent @event) => canHandle(@event);

        public Task HandleAsync(AgentEvent @event, ITuiRenderContext context, CancellationToken ct)
        {
            Calls++;
            return onHandle?.Invoke(@event) ?? Task.CompletedTask;
        }
    }

    [Test]
    public async Task Dispatch_RoutesEvent_OnlyToMatchingHandlers()
    {
        var renderer = new HandlerTestRenderer();
        var match = new StubHandler(e => e is MessageStartEvent);
        var miss = new StubHandler(e => e is AgentErrorEvent);
        renderer.Add(match);
        renderer.Add(miss);

        await renderer.Dispatch(TestEvents.MessageStart(AssistantMessage.Empty("s1", "m1")));

        await Assert.That(match.Calls).IsEqualTo(1);
        await Assert.That(miss.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Dispatch_WithNoHandlers_IsNoOp()
    {
        var renderer = new HandlerTestRenderer();
        await renderer.Dispatch(TestEvents.TurnStart());
        await Assert.That(renderer.Capture.Output).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Dispatch_HandlerThrow_DoesNotBlockOtherHandlers()
    {
        var renderer = new HandlerTestRenderer();
        var failing = new StubHandler(
            e => e is MessageStartEvent,
            _ => Task.FromException(new InvalidOperationException("boom")));
        var next = new StubHandler(e => e is MessageStartEvent);
        renderer.Add(failing);
        renderer.Add(next);

        await renderer.Dispatch(TestEvents.MessageStart(AssistantMessage.Empty("s1", "m1")));

        await Assert.That(failing.Calls).IsEqualTo(1);
        await Assert.That(next.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Dispatch_CanHandleThrow_DoesNotBlockOtherHandlers()
    {
        var renderer = new HandlerTestRenderer();
        var failing = new ExplodingCanHandleHandler();
        var next = new StubHandler(_ => true);
        renderer.Add(failing);
        renderer.Add(next);

        await renderer.Dispatch(TestEvents.TurnStart());

        await Assert.That(next.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task RegisterHandler_Null_Throws()
    {
        var renderer = new HandlerTestRenderer();
        await Assert.That(() => renderer.Add(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task GuardedSetup_WithoutBackendDetail_StillLogsTheFailure()
    {
        var logger = new CapturingLogger();
        var renderer = new HandlerTestRenderer(logger);

        Result result = await renderer.Guarded(static () => throw new InvalidOperationException("setup exploded"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(logger.Capture.DetailedEntries.Any(e =>
                e.Level == LogLevel.Error && e.EventId == 1 && e.Message.Contains("initialization failed")))
            .IsTrue()
            .Because("the caller only sees Result.Failure(message); event 1 is the exception's only record");
    }

    [Test]
    public async Task GuardedSetup_WithBackendDetail_LogsTheDetail()
    {
        var logger = new CapturingLogger();
        var renderer = new HandlerTestRenderer(logger);

        Result result = await renderer.Guarded(
            static () => throw new InvalidOperationException("setup exploded"),
            "backend prelude failed");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(logger.Capture.DetailedEntries.Any(e =>
                e.Level == LogLevel.Error && e.EventId == 2 && e.Message.Contains("backend prelude failed")))
            .IsTrue()
            .Because("the backend message is the extra detail, not the condition for logging at all");
    }

    private sealed class ExplodingCanHandleHandler : IAgentEventHandler
    {
        public bool CanHandle(AgentEvent @event) => throw new InvalidOperationException("boom");

        public Task HandleAsync(AgentEvent @event, ITuiRenderContext context, CancellationToken ct)
            => Task.CompletedTask;
    }
}
