using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Abstractions.Tui;
using Harbor.Core.Configuration;
#if HARBOR_WITH_ALL_PROVIDERS
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
using Harbor.Providers.OpenAiCompatible;
#endif
namespace Harbor.Cli.Hosting;
#if HARBOR_WITH_ALL_PROVIDERS
/// <summary>Adapter that resolves API key via AuthStore.</summary>
/// <remarks>
///     Excluded when <c>HARBOR_WITH_ALL_PROVIDERS</c> is undefined — the
///     interfaces it implements (IAnthropicAuthResolver, IOpenAIAuthResolver,
///     IAuthResolver) live in Harbor.Providers.{Anthropic,OpenAI,OpenAiCompatible}
///     which are removed from the project reference graph when
///     HarborWithAllProviders=false. Ollama (always included) has no auth
///     resolver because OllamaLlmClient doesn't take an auth resolver — it
///     talks to a local daemon that doesn't require an API key.
/// </remarks>
internal sealed class ConfigAuthResolver : IAnthropicAuthResolver, IOpenAIAuthResolver, IAuthResolver
{
    private readonly AuthStore _authStore;
    private readonly string _providerId;

    public ConfigAuthResolver(AuthStore authStore, string providerId)
    {
        _authStore = authStore;
        _providerId = providerId;
    }

    public Task<Result<string>> ResolveApiKeyAsync(CancellationToken ct = default)
        => _authStore.GetApiKeyAsync(_providerId, ct);

    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default)
        => _authStore.GetApiKeyAsync(string.IsNullOrEmpty(providerId) ? _providerId : providerId, ct);
}
#endif

/// <summary>Simple ICommandContext for REPL.</summary>
internal sealed class SimpleCommandContext : ICommandContext
{
    public SimpleCommandContext(Session session, IAgent agent, IProviderRegistry providers,
        IToolRegistry tools, Action<string> output, Func<string, Task<string>> prompt)
    {
        Session = new DummySessionContext(session);
        Agent = agent;
        Providers = providers;
        Tools = tools;
        Output = output;
        Prompt = prompt;
    }
    public ISessionContext Session { get; }
    public IAgent Agent { get; }
    public IProviderRegistry Providers { get; }
    public IToolRegistry Tools { get; }
    public Action<string> Output { get; }
    public Func<string, Task<string>> Prompt { get; }
}

internal sealed class DummySessionContext : ISessionContext
{
    public DummySessionContext(Session session) { Session = session; }
    public Session Session { get; }
    public IReadOnlyList<AgentMessage> Messages => Array.Empty<AgentMessage>();

    // A4: the channel MUST be created once per context. A per-access factory
    // (=> Channel.CreateUnbounded<...>()) silently dropped every steering
    // message — writers and the AgentLoop reader held different channels.
    public Channel<AgentMessage> SteeringQueue { get; } = Channel.CreateUnbounded<AgentMessage>();
    public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateStatsAsync(Usage usage, CancellationToken ct = default) => Task.CompletedTask;
}
