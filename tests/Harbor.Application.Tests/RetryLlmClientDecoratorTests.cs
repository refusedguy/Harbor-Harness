using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Application.Resilience;
using TUnit.Assertions;
using FakeLlmClientBase = Harbor.TestKit.Generated.FakeLlmClient; // #1769: generated double


namespace Harbor.Application.Tests;

/// <summary>
///     #1502: the SSE pump itself never retries — a transient at the provider
///     seam surfaces as a terminal <see cref="ErrorEvent" /> on the first
///     occurrence. <see cref="RetryLlmClientDecorator" /> closes that gap by
///     driving the <b>preflight</b> (first <c>MoveNextAsync</c> of the inner
///     stream) through the canonical <see cref="IRetryPolicy" />. These tests
///     pin the two invariants the issue calls out:
///     <list type="bullet">
///         <item>a transient failure <i>before the first delivered token</i> is
///         retried under the shared policy, invisibly to the consumer;</item>
///         <item>a failure <i>after</i> any event was delivered is NEVER
///         silently restarted — mid-stream errors pass through untouched.</item>
///     </list>
/// </summary>
public class RetryLlmClientDecoratorTests
{
    /// <summary>
    ///     Transient exceptions before the first token are replayed — the
    ///     consumer observes one seamless successful stream and never sees
    ///     the failed attempts.
    /// </summary>
    [Test]
    public async Task StreamException_PreflightTransient_IsRetried()
    {
        var inner = new ScriptedLlmClient(
            ThenThrow(null, new HttpRequestException("no such host", null)),
            Events(new FinishEvent()));
        var decorator = Make(inner);

        var (events, error) = await Capture(decorator.StreamAsync(NewRequest()));

        await Assert.That(error).IsNull().Because("the retry recovered a transient connect blip — it must be invisible to the consumer");
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]).IsTypeOf<FinishEvent>();
        await Assert.That(inner.Calls).IsEqualTo(2).Because("one failed preflight + one successful one");
    }

    /// <summary>
    ///     A transient <see cref="ErrorEvent" /> (the pump's error channel —
    ///     HTTP 429/5xx/status failures all ride it, ROP-A ПР.5) as the FIRST
    ///     event is consumed by the decorator and the stream restarts; the
    ///     consumer sees only the fresh attempt.
    /// </summary>
    [Test]
    public async Task TransientErrorEvent_FirstEvent_IsConsumedAndRetried()
    {
        var inner = new ScriptedLlmClient(
            Events(new ErrorEvent("HTTP 429: slow down", Kind: ProviderErrorKind.RateLimit, StatusCode: 429)),
            Events(new TextDeltaEvent("t1", "ok"), new FinishEvent()));
        var decorator = Make(inner);

        var (events, error) = await Capture(decorator.StreamAsync(NewRequest()));

        await Assert.That(error).IsNull();
        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[0]).IsTypeOf<TextDeltaEvent>();
        await Assert.That(events[1]).IsTypeOf<FinishEvent>();
        await Assert.That(inner.Calls).IsEqualTo(2).Because("the 429 must be replayed with a fresh attempt, never delivered");
    }

    /// <summary>
    ///     THE mid-stream guarantee: the first delivered event closes the
    ///     retry window. A transient failure after it propagates untouched —
    ///     the consumer keeps its data exactly once and sees the failure; no
    ///     second attempt ever runs.
    /// </summary>
    [Test]
    public async Task StreamException_AfterFirstDeliveredEvent_TheStreamIsNeverRestarted()
    {
        var inner = new ScriptedLlmClient(
            ThenThrow(new TextDeltaEvent("t1", "once only"), new HttpRequestException("connection reset", null)));
        var decorator = Make(inner);

        var (events, error) = await Capture(decorator.StreamAsync(NewRequest()));

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]).IsTypeOf<TextDeltaEvent>();
        await Assert.That(error).IsNotNull().Because("after the first token, a retry would replay delivered bytes — propagate instead");
        await Assert.That(error).IsTypeOf<HttpRequestException>();
        await Assert.That(inner.Calls).IsEqualTo(1).Because("no attempt may be spent after delivery started");
    }

    /// <summary>
    ///     Mid-stream <i>transient</i> <see cref="ErrorEvent" /> — same
    ///     guarantee: it already followed delivered output, so it is delivered
    ///     to the consumer verbatim (the outer turn-level budgets decide what
    ///     to do with it), never replayed by the decorator.
    /// </summary>
    [Test]
    public async Task TransientErrorEvent_AfterFirstDeliveredEvent_IsDeliveredNotReplayed()
    {
        var inner = new ScriptedLlmClient(
            Events(new TextDeltaEvent("t1", "once only"), new ErrorEvent("mid-stream 429", Kind: ProviderErrorKind.RateLimit, StatusCode: 429)));
        var decorator = Make(inner);

        var (events, error) = await Capture(decorator.StreamAsync(NewRequest()));

        await Assert.That(error).IsNull().Because("an in-stream terminal error is delivered as the event it always was");
        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[0]).IsTypeOf<TextDeltaEvent>();
        var deliveredError = events[1] as ErrorEvent;
        await Assert.That(deliveredError).IsNotNull();
        await Assert.That(deliveredError!.Kind).IsEqualTo(ProviderErrorKind.RateLimit);
        await Assert.That(inner.Calls).IsEqualTo(1).Because("no attempt may be spent after delivery started");
    }

    /// <summary>
    ///     Budget exhaustion on a preflight transient preserves the delivery
    ///     contract: exactly one terminal <see cref="ErrorEvent" /> on the
    ///     stream, never a thrown exception from <c>StreamAsync</c>.
    /// </summary>
    [Test]
    public async Task TransientPreflight_BudgetExhausted_DeliversSingleErrorEvent()
    {
        var inner = new ScriptedLlmClient(
            Events(new ErrorEvent("down", Kind: ProviderErrorKind.Network)),
            Events(new ErrorEvent("still down", Kind: ProviderErrorKind.Network)));
        var decorator = Make(inner, new RetryOptions(2, TimeSpan.FromMilliseconds(1), false));

        var (events, error) = await Capture(decorator.StreamAsync(NewRequest()));

        await Assert.That(error).IsNull();
        await Assert.That(events.Count).IsEqualTo(1);
        var delivered = events[0] as ErrorEvent;
        await Assert.That(delivered).IsNotNull();
        await Assert.That(delivered!.Kind).IsEqualTo(ProviderErrorKind.Network);
        await Assert.That(inner.Calls).IsEqualTo(2).Because("the budget caps the attempts");
    }

    /// <summary>
    ///     Rate-limit preflight failures draw from the separate minutes-scale
    ///     budget (#270), which arrives via <see cref="LlmStreamErrorException" />
    ///     carrying the provider <see cref="ProviderErrorKind" />.
    /// </summary>
    [Test]
    public async Task RateLimitPreflight_UsesSeparateBudget()
    {
        var inner = new ScriptedLlmClient(
            Events(new ErrorEvent("HTTP 429", Kind: ProviderErrorKind.RateLimit, StatusCode: 429)),
            Events(new ErrorEvent("HTTP 429", Kind: ProviderErrorKind.RateLimit, StatusCode: 429)));
        var decorator = Make(inner, new RetryOptions(
            MaxAttempts: 1,
            BaseDelay: TimeSpan.FromMilliseconds(1),
            UseJitter: false,
            MaxRateLimitAttempts: 2,
            RateLimitBaseDelay: TimeSpan.FromMilliseconds(1)));

        var (events, error) = await Capture(decorator.StreamAsync(NewRequest()));

        await Assert.That(error).IsNull();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]).IsTypeOf<ErrorEvent>();
        var rateLimited = events[0] as ErrorEvent;
        await Assert.That(rateLimited!.Kind).IsEqualTo(ProviderErrorKind.RateLimit);
        await Assert.That(inner.Calls).IsEqualTo(2).Because("the rate-limit budget (2), not the general one (1), governs 429s");
    }

    /// <summary>
    ///     A fatal first <see cref="ErrorEvent" /> (401/400-style — retrying a
    ///     bad key cannot fix it) is delivered verbatim on attempt #1.
    /// </summary>
    [Test]
    public async Task FatalErrorEvent_First_IsDeliveredImmediately()
    {
        var inner = new ScriptedLlmClient(
            Events(new ErrorEvent("Invalid API key", Kind: ProviderErrorKind.Auth, StatusCode: 401)));
        var decorator = Make(inner);

        var (events, error) = await Capture(decorator.StreamAsync(NewRequest()));

        await Assert.That(error).IsNull();
        await Assert.That(events.Count).IsEqualTo(1);
        var delivered = events[0] as ErrorEvent;
        await Assert.That(delivered).IsNotNull();
        await Assert.That(delivered!.Kind).IsEqualTo(ProviderErrorKind.Auth);
        await Assert.That(inner.Calls).IsEqualTo(1).Because("fatal kinds are never retried");
    }

    /// <summary>Graceful empty stream (no events at all) passes through.</summary>
    [Test]
    public async Task EmptyStream_PassesThrough()
    {
        var inner = new ScriptedLlmClient(Events());
        var decorator = Make(inner);

        var (events, error) = await Capture(decorator.StreamAsync(NewRequest()));

        await Assert.That(error).IsNull();
        await Assert.That(events).IsEmpty();
        await Assert.That(inner.Calls).IsEqualTo(1);
    }

    /// <summary><see cref="RetryLlmClientDecorator.GetModelsAsync" /> is pass-through.</summary>
    [Test]
    public async Task GetModelsAsync_DelegatesToInner()
    {
        var modelList = Array.Empty<ModelInfo>();
        var inner = new ScriptedLlmClient { ModelsResult = Result.Success<IReadOnlyList<ModelInfo>>(modelList) };
        var decorator = Make(inner);

        Result<IReadOnlyList<ModelInfo>> result = await decorator.GetModelsAsync(CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsSameReferenceAs(modelList);
    }

    // ── fixtures ──

    /// <summary>Hermetic budget: 1 ms backoff, no jitter, like RetryPolicyTests.Opts — no wall-clock flake on loaded runners.</summary>
    private static RetryLlmClientDecorator Make(ScriptedLlmClient inner, RetryOptions? options = null) =>
        new(inner, new RetryPolicy(), options: options ?? new RetryOptions(3, TimeSpan.FromMilliseconds(1), false));

    private static LlmRequest NewRequest() =>
        new("m", [LlmUserMessage.Text("hi")], "", []);

    /// <summary>Collects a stream, capturing a mid-stream exception instead of throwing it into the test harness.</summary>
    private static async Task<(List<LlmEvent> Events, Exception? Error)> Capture(IAsyncEnumerable<LlmEvent> stream)
    {
        var events = new List<LlmEvent>();
        try
        {
            await foreach (LlmEvent evt in stream.ConfigureAwait(false))
            {
                events.Add(evt);
            }
        }
        catch (Exception ex)
        {
            return (events, ex);
        }
        return (events, null);
    }

    private static async IAsyncEnumerable<LlmEvent> Events(params LlmEvent[] events)
    {
        foreach (LlmEvent evt in events)
        {
            yield return evt;
        }
    }

    /// <summary>
    ///     Yields <paramref name="first" /> when non-null, then fails the next
    ///     move with <paramref name="ex" />. One helper for both shapes
    ///     (immediate-preflight-failure and fail-after-first-delivery): the
    ///     conditional yield is what keeps this an async iterator (CS8419).
    /// </summary>
    private static async IAsyncEnumerable<LlmEvent> ThenThrow(LlmEvent? first, Exception ex)
    {
        if (first is not null)
        {
            yield return first;
        }

        await Task.Yield();
        throw ex;
    }

    /// <summary>
    ///     Inner client with one scripted stream per attempt. Every
    ///     <see cref="StreamAsync" /> call consumes the next script, which
    ///     makes <c>Calls</c> the exact attempt counter the assertions above
    ///     pin.
    /// </summary>
    private sealed class ScriptedLlmClient : FakeLlmClientBase
    {
        private readonly List<IAsyncEnumerable<LlmEvent>> _scripts;

        public ScriptedLlmClient(params IAsyncEnumerable<LlmEvent>[] scripts)
        {
            _scripts = scripts.ToList();
            ProviderId = ProviderId.Create("fake");
        }

        public int Calls { get; private set; }

        public Result<IReadOnlyList<ModelInfo>>? ModelsResult { get; set; }

        public override IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            _scripts[Calls++];

        public override Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ModelsResult ?? Result.Success<IReadOnlyList<ModelInfo>>([]));
    }
}
