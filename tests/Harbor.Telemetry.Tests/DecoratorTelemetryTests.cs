using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Diagnostics;
using Harbor.Telemetry;
using TUnit.Assertions;

namespace Harbor.Telemetry.Tests;

/// <summary>
///     sprint3-C T.4: decorator behaviour verified with plain
///     <see cref="ActivityListener" /> / <see cref="MeterListener" /> — no OTEL.
///     Covers: span started/ended/exception/attributes and metric emissions.
/// </summary>
[NotInParallel("telemetry")]
public class DecoratorTelemetryTests : IDisposable
{
    private readonly List<Activity> _stoppedSpans = [];
    private readonly List<(string Instrument, double Value, string? Status)> _metrics = [];
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;

    public DecoratorTelemetryTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == HarborTelemetrySources.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => _stoppedSpans.Add(activity),
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener();
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == HarborTelemetrySources.SourceName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            string? status = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key is TelemetryTagNames.ToolStatus or TelemetryTagNames.LlmTokenType or "turn.status")
                {
                    status = tag.Value?.ToString();
                }
            }

            _metrics.Add((instrument.Name, value, status));
        });
        _meterListener.Start();
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }

    // ── Tool decorator ───────────────────────────────────────────────────

    [Test]
    public async Task ToolDecorator_Success_RecordsSpanCounterAndHistogram()
    {
        var registry = new InstrumentedToolRegistry(
            new SingleToolRegistry(new StubTool("read", () => StubTool.Outcome.Success)),
            new MeterMetrics(),
            new ActivityTracer());

        Result<ITool> resolved = registry.GetTool(ToolName.Create("read"));
        await Assert.That(resolved.IsSuccess).IsTrue();
        ToolResult result = await resolved.Value.ExecuteAsync(Args(), DefaultContext());

        await Assert.That(result.IsError).IsFalse();

        Activity? span = _stoppedSpans.FirstOrDefault(a => a.DisplayName == "tool.execute");
        await Assert.That(span).IsNotNull();
        await Assert.That(span!.GetTagItem(TelemetryTagNames.ToolName)).IsEqualTo("read");
        await Assert.That(span.GetTagItem(TelemetryTagNames.ToolStatus)).IsEqualTo("ok");

        await Assert.That(_metrics.Any(m => m.Instrument == TelemetryTagNames.ToolCalls && m.Status == "ok")).IsTrue();
        await Assert.That(_metrics.Any(m => m.Instrument == TelemetryTagNames.ToolDurationMs && m.Value >= 0)).IsTrue();
    }

    [Test]
    public async Task ToolDecorator_ErrorResult_MarksSpanAndCountsError()
    {
        var registry = new InstrumentedToolRegistry(
            new SingleToolRegistry(new StubTool("write", () => StubTool.Outcome.Error)),
            new MeterMetrics(),
            new ActivityTracer());

        Result<ITool> resolved = registry.GetTool(ToolName.Create("write"));
        ToolResult result = await resolved.Value.ExecuteAsync(Args(), DefaultContext());

        await Assert.That(result.IsError).IsTrue();
        Activity? span = _stoppedSpans.FirstOrDefault(a => a.DisplayName == "tool.execute");
        await Assert.That(span!.GetTagItem(TelemetryTagNames.ToolStatus)).IsEqualTo("error");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(_metrics.Any(m => m.Instrument == TelemetryTagNames.ToolCalls && m.Status == "error")).IsTrue();
    }

    [Test]
    public async Task ToolDecorator_ThrownException_RethrowsWithFailedSpan()
    {
        var registry = new InstrumentedToolRegistry(
            new SingleToolRegistry(new StubTool("bash", () => StubTool.Outcome.Throw)),
            new MeterMetrics(),
            new ActivityTracer());

        Result<ITool> resolved = registry.GetTool(ToolName.Create("bash"));

        await Assert.That(async () => await resolved.Value.ExecuteAsync(Args(), DefaultContext()))
            .Throws<InvalidOperationException>();

        Activity? span = _stoppedSpans.FirstOrDefault(a => a.DisplayName == "tool.execute");
        await Assert.That(span!.GetTagItem(TelemetryTagNames.ToolStatus)).IsEqualTo("exception");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    // ── LLM client decorator ─────────────────────────────────────────────

    [Test]
    public async Task LlmClientDecorator_RecordsTtfbTokensAndOkStatus()
    {
        var client = new InstrumentedLlmClient(
            ProviderId.Create("test"),
            new Harbor.TestKit.ScriptedLlmClient(
            [
                new LlmEvent[]
                {
                    new TextDeltaEvent("t1", "hello"),
                    new StepFinishEvent(0, "stop", new Usage(10, 5)),
                    new StepFinishEvent(1, "stop", new Usage(0, 0)),
                }
            ]),
            new MeterMetrics(),
            new ActivityTracer());

        List<LlmEvent> seen = [];
        await foreach (LlmEvent evt in client.StreamAsync(Request(), CancellationToken.None))
        {
            seen.Add(evt);
        }

        await Assert.That(seen.Count).IsEqualTo(3);

        Activity? span = _stoppedSpans.FirstOrDefault(a => a.DisplayName == "llm.stream");
        await Assert.That(span).IsNotNull();
        await Assert.That(span!.GetTagItem(TelemetryTagNames.Model)).IsEqualTo("test-model");
        await Assert.That(span.GetTagItem(TelemetryTagNames.StreamStatus)).IsEqualTo("ok");

        double ttfb = _metrics.Where(m => m.Instrument == TelemetryTagNames.LlmTtfbMs).Sum(m => m.Value);
        await Assert.That(ttfb).IsGreaterThan(0);

        double promptTokens = _metrics
            .Where(m => m.Instrument == TelemetryTagNames.LlmTokens && m.Status == "prompt").Sum(m => m.Value);
        double completionTokens = _metrics
            .Where(m => m.Instrument == TelemetryTagNames.LlmTokens && m.Status == "completion").Sum(m => m.Value);
        await Assert.That(promptTokens).IsEqualTo(10);
        await Assert.That(completionTokens).IsEqualTo(5);
    }

    [Test]
    public async Task LlmClientDecorator_InnerThrow_PropagatesAndMarksSpan()
    {
        var client = new InstrumentedLlmClient(
            ProviderId.Create("test"),
            new Harbor.TestKit.ThrowingLlmClient(),
            new MeterMetrics(),
            new ActivityTracer());

        await Assert.That(async () =>
        {
            await foreach (LlmEvent _ in client.StreamAsync(Request(), CancellationToken.None))
            {
            }
        }).Throws<InvalidOperationException>();

        Activity? span = _stoppedSpans.FirstOrDefault(a => a.DisplayName == "llm.stream");
        await Assert.That(span!.GetTagItem(TelemetryTagNames.StreamStatus)).IsEqualTo("exception");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    // ── Agent proxy ──────────────────────────────────────────────────────

    [Test]
    public async Task AgentProxy_SuccessTurn_SetsCorrelationAndMetrics()
    {
        Session session = NewSession();
        CorrelationContext? seenInside = null;
        var agent = new TracingAgentProxy(
            new StubAgent(_ => Result.Success(), ctx => seenInside = ctx),
            new MeterMetrics(),
            new ActivityTracer());
        agent.Initialize(session, Definition());

        var result = await agent.PromptAsync("hi");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(seenInside).IsNotNull();
        await Assert.That(seenInside!.SessionId).IsEqualTo(session.Id);
        await Assert.That(seenInside.AgentName).IsEqualTo("code");

        Activity? span = _stoppedSpans.FirstOrDefault(a => a.DisplayName == "agent.turn");
        await Assert.That(span).IsNotNull();
        await Assert.That(span!.GetTagItem(TelemetryTagNames.Agent)).IsEqualTo("code");
        await Assert.That(_metrics.Any(m => m.Instrument == TelemetryTagNames.TurnCount && m.Status == "ok")).IsTrue();
    }

    [Test]
    public async Task AgentProxy_FailedTurn_MarksSpanError()
    {
        var agent = new TracingAgentProxy(
            new StubAgent(_ => Result.Failure("boom"), _ => { }),
            new MeterMetrics(),
            new ActivityTracer());
        agent.Initialize(NewSession(), Definition());

        var result = await agent.PromptAsync("hi");

        await Assert.That(result.IsFailure).IsTrue();
        Activity? span = _stoppedSpans.FirstOrDefault(a => a.DisplayName == "agent.turn");
        await Assert.That(span!.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    // ── Provider registry decorator ──────────────────────────────────────

    [Test]
    public async Task ProviderRegistry_GetClient_ReturnsInstrumentedClient()
    {
        var registry = new InstrumentedProviderRegistry(
            new SingleProviderRegistry(new Harbor.TestKit.ScriptedLlmClient([]), ProviderId.Create("test")),
            new MeterMetrics(),
            new ActivityTracer());

        Result<ILlmClient> resolved = registry.GetClient(ProviderId.Create("test"));

        await Assert.That(resolved.GetValueOrDefault()).IsTypeOf<InstrumentedLlmClient>();
    }

    // ── Harness ──────────────────────────────────────────────────────────

    private static JsonElement Args() => JsonDocument.Parse("{}").RootElement.Clone();

    private static ToolContext DefaultContext() => new(
        "session-tel-1",
        "assistant-tel-1",
        "call-tel-1",
        "code",
        CancellationToken.None,
        [],
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);

    private static LlmRequest Request() => new(
        "test-model",
        [],
        "system",
        []);

    private static Session NewSession() =>
        Session.Create("/tmp/harbor-telemetry-tests", "code", "test", "test-model");

    private static AgentDefinition Definition() => new(
        AgentName.Create("code"),
        "Code",
        "telemetry harness",
        "test-model",
        "test",
        new PermissionRuleset([new PermissionRule("*", "*", PermissionAction.Allow)]));

    private sealed class SingleToolRegistry(ITool tool) : IToolRegistry
    {
        public IReadOnlyList<ToolDescriptor> GetAllTools() => [];

        public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null) => [];

        public Result<ITool> GetTool(ToolName name) => Result.Success(tool);

        public Result Register(ITool tool) => Result.Success();

        public Result Unregister(ToolName name) => Result.Success();
    }

    private sealed class SingleProviderRegistry(ILlmClient client, ProviderId id) : IProviderRegistry
    {
        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [id];

        public Result<ILlmClient> GetClient(ProviderId providerId) =>
            providerId == id ? Result.Success(client) : Result.Failure<ILlmClient>("unknown");

        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([]));

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([]));

        public void Register(ProviderId providerId, Func<ILlmClient> factory)
        {
        }

        public Result Unregister(ProviderId providerId) => Result.Success();
    }

    private sealed class StubTool(string name, Func<StubTool.Outcome> outcomeFactory) : ITool
    {
        public enum Outcome { Success, Error, Throw }

        public ToolName Name => ToolName.Create(name);

        public string DisplayName => name;

        public string Description => "stub";

        public ExecutionMode ExecutionMode => ExecutionMode.Sequential;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(outcomeFactory() switch
            {
                Outcome.Error => ToolResult.Error("stub error"),
                Outcome.Throw => throw new InvalidOperationException("stub threw"),
                _ => ToolResult.Success("stub ok"),
            });
    }

    private sealed class StubAgent(Func<string, Result> outcome, Action<CorrelationContext> onPrompt) : IAgent
    {
        public AgentState State { get; private set; } = null!;

        public CancellationTokenSource AbortSource { get; } = new();

        public void ResetAbortSource()
        {
        }

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => new Noop();

        public void Initialize(Session session, AgentDefinition agent) => State = AgentState.Idle(session.Id, agent);

        public void Steer(AgentMessage message)
        {
        }

        public Task<Result> PromptAsync(string text, CancellationToken ct = default)
        {
            onPrompt(Correlation.Current);
            return Task.FromResult(outcome(text));
        }

        public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void Dispose()
        {
        }

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
