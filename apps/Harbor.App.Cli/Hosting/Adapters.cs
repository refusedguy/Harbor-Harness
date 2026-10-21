using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Abstractions.Tui;
using Harbor.Application.Configuration;
#if HARBOR_WITH_ALL_PROVIDERS
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
using Harbor.Providers.OpenAiCompatible;
#endif
namespace Harbor.App.Cli.Hosting;

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

    // #653: the pricing rides along because the model is only known to the
    // caller; this stand-in records nothing, so it has nothing to price with.
    public Task UpdateStatsAsync(Usage usage, Pricing pricing, CancellationToken ct = default) => Task.CompletedTask;

    // #1765: the durability checkpoint. This stand-in records nothing at all,
    // so there is no state that could fail to survive a restart. The answer
    // is the honest one, and it is an override rather than the interface's
    // failing default because a stand-in is not a store.
    public Task<Result> CheckpointAsync(string reason, CancellationToken ct = default) => Task.FromResult(Result.Success());
}
