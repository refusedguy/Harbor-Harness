using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Configuration;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Fresh session: create, rebind agent, reset timeline/selection/composer.</summary>
internal sealed class NewSessionCommand : IReplCommand
{
    public string Id => "new";
    public IReadOnlyList<string> Aliases => ["new-session"];
    public string Title => "New Session";
    public string Description => "start a fresh chat session";
    public string Group => "Sessions";

    public async Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;
        if (host.Agent.State.IsRunning)
        {
            host.Bridge.AppendSystemLine("⚠ Cannot create session while running");
            host.WakeUp();
            return;
        }

        var store = host.SessionStore;
        if (store is null)
        {
            host.Bridge.AppendSystemLine("⇄ сессия недоступна: хост без хранилища сессий");
            host.WakeUp();
            return;
        }

        var configResult = await host.ConfigStore
            .LoadAsync(ct).ConfigureAwait(false);
        // #599: derived from IdentityConfig, never spelled here — a config that
        // failed to load must not become a second home for the default model id.
        string provider = configResult.IsSuccess
            ? configResult.Value.Provider
            : IdentityConfig.FallbackProvider;
        // A session records the BARE model id (the provider travels beside it), so
        // this is the model half of the same single source.
        string model = configResult.IsSuccess
            ? configResult.Value.Model
            : IdentityConfig.FallbackModelRef.ModelId;

        var newSession = await store.CreateAsync(Environment.CurrentDirectory, host.SessionModel.Agent, provider, model, ct).ConfigureAwait(false);
        if (newSession.IsFailure)
        {
            host.Bridge.AppendSystemLine($"! {newSession.Error}");
            host.WakeUp();
            return;
        }

        var agentDef = host.AgentRegistry
            .GetAgent(AgentName.Create(host.SessionModel.Agent));
        if (agentDef.IsFailure)
        {
            host.Bridge.AppendSystemLine($"! {agentDef.Error}");
            host.WakeUp();
            return;
        }

        host.Agent.Initialize(newSession.Value, agentDef.Value);
        host.SessionModel = newSession.Value;
        host.Bridge.ResetMessageTracking();
        host.Timeline.Clear();
        host.Selection.Clear();
        host.Composer.Buffer.Clear();
        host.ScrollTimelineToEnd();
        if (host.Screen.Sidebar is { } sb)
        {
            sb.State = sb.State with
            {
                #pragma warning disable CFE0001
                // CFE0001 baseline: docs/ROP-API-INVENTORY.md 5.
                // Guard upstream is an early return.
                SessionId = newSession.Value.Id,
                #pragma warning restore CFE0001
                #pragma warning disable CFE0001
                // CFE0001 baseline: docs/ROP-API-INVENTORY.md 5.
                // Guard upstream is an early return.
                SessionTitle = newSession.Value.Title,
                #pragma warning restore CFE0001
                Model = $"{provider}/{model}",
                #pragma warning disable CFE0001
                // CFE0001 baseline: docs/ROP-API-INVENTORY.md 5.
                // Guard upstream is an early return.
                Agent = newSession.Value.Agent,
                #pragma warning restore CFE0001
                MessageCount = 0,
                ContextWindow = await host.ResolveContextWindowAsync(provider, model, ct).ConfigureAwait(false),
                // Fresh session starts counting from zero — otherwise the
                // previous session's totals linger in the sidebar.
                TokensIn = 0,
                TokensOut = 0,
                CostUsd = 0,
            };
        }

        await host.SyncSessionsToStoreAsync(ct).ConfigureAwait(false);
        host.Bridge.AppendSystemLine($"✓ Started fresh session: {newSession.Value.Id[..Math.Min(8, newSession.Value.Id.Length)]}");
        host.WakeUp();
    }
}
