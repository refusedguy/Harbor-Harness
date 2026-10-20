using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using Harbor.Registries.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Core.Tests;

/// <summary>
///     Shared fixture for the compaction suites: the summarization request is
///     always driven through a <see cref="CompactionService" /> that talks to a
///     scripted client, against a model with a huge context window so
///     ShouldCompact-style budgeting never interferes.
///     <para>
///         What varies between the suites is WHICH client is scripted (a
///         summarizer, a silent one, a hanging one) and WHAT the service is
///         configured with (the secondary model, the keep-recent budget) —
///         so that is what this base parameterises. The model, the message
///         factories, the history and the capturing client are the invariant,
///         and they live here once.
///     </para>
///     <para>
///         Why this base lives in the test project and NOT in
///         <c>Harbor.TestKit</c>: it composes
///         <c>Harbor.Application.Sessions.CompactionService</c> and
///         <c>Harbor.Registries.Providers.ProviderRegistry</c>, and a
///         TestKit-hosted base would push the registries assembly into the
///         closure of every TestKit consumer — the epic's rule 3 forbids
///         exactly that. A base class in one test project referenced by
///         another would be worse still: a cycle.
///     </para>
/// </summary>
public abstract class CompactionTestBase
{
    /// <summary>
    ///     The model every suite compacts against: a huge context window, so
    ///     the head cut is deterministic and nothing else interferes.
    /// </summary>
    protected static ModelInfo TestModel(
        string id = "test-model",
        string provider = "test",
        string displayName = "Test Model") =>
        new(id, provider, displayName, 1_000_000, 1_024, false, false, true, Pricing.Unknown, "openai");

    /// <summary>The passed model with a huge window so ShouldCompact-style budgeting never interferes.</summary>
    protected static ModelInfo HugeWindow(ModelInfo source) => source with { ContextWindow = 1_000_000 };

    protected static UserMessage User(string content) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        content,
        "code",
        "test-model");

    protected static AssistantMessage Assistant(string text) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        new[] { new TextPart(text) },
        StopReason.Stop,
        new Usage(0, 0),
        "test-model");

    /// <summary>Six-message history large enough for a deterministic head cut.</summary>
    protected static List<AgentMessage> SixMessageHistory() =>
    [
        User("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        Assistant("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        User("cccccccccccccccccccccccccccccccccccccccc"),
        Assistant("dddddddddddddddddddddddddddddddddddd"),
        User("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
        Assistant("ffffffffffffffffffffffffffffffffffff")
    ];

    /// <summary>
    ///     A compaction service over the registered providers, with the
    ///     configuration each suite varies made explicit. A null
    ///     <paramref name="client" /> registers no provider at all — the shape
    ///     the constructor-only (#1582) tests need.
    /// </summary>
    protected static CompactionService CreateService(
        ILlmClient? client = null,
        string? secondaryModel = null,
        int keepRecentTokens = 500,
        int tailTurns = 0,
        (string Provider, ILlmClient Client)? extraProvider = null,
        ILogger<CompactionService>? logger = null)
    {
        var providers = new ProviderRegistry();
        if (client is not null)
        {
            providers.Register(ProviderId.Create("test"), () => client!);
        }

        if (extraProvider is { } extra)
        {
            providers.Register(ProviderId.Create(extra.Provider), () => extra.Client);
        }

        return new CompactionService(
            new TokenTracker(),
            providers,
            logger ?? NullLogger<CompactionService>.Instance,
            secondaryModel)
        {
            KeepRecentTokens = keepRecentTokens,
            TailTurns = tailTurns,
        };
    }

    /// <summary>
    ///     Log capture for the #1582 observability tests: records the formatted
    ///     message of every entry, warnings included.
    /// </summary>
    protected sealed class CapturingLogger : ILogger<CompactionService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    /// <summary>Capturing client bound to a fixed provider id and catalog.</summary>
    protected sealed class CapturingLlmClient(string summary, ModelInfo[] catalog) : ILlmClient
    {
        public List<LlmRequest> Requests { get; } = [];

        public ProviderId ProviderId { get; init; } = ProviderId.Create("test");

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new TextDeltaEvent("0", summary);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            yield return new StepFinishEvent(0, "stop", new Usage(0, summary.Length / 4));
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(catalog));
    }
}
