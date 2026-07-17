using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Abstractions.Tui;
using Harbor.Core.Configuration;
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
using Harbor.Providers.OpenAiCompatible;
namespace Harbor.Cli.Hosting;
/// <summary>Adapter that resolves API key via AuthStore.</summary>
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
    public Channel<AgentMessage> SteeringQueue => Channel.CreateUnbounded<AgentMessage>();
    public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateStatsAsync(Usage usage, CancellationToken ct = default) => Task.CompletedTask;
}
