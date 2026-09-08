using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        var store = host.Services.GetService<ISessionStore>();
        if (store is null)
        {
            host.Bridge.AppendSystemLine("⇄ сессия недоступна: хост без хранилища сессий");
            host.WakeUp();
            return;
        }

        var configResult = await host.Services.GetRequiredService<IConfigStore>()
            .LoadAsync(ct).ConfigureAwait(false);
        string provider = configResult.IsSuccess ? configResult.Value.Provider : "kilocode";
        string model = configResult.IsSuccess ? configResult.Value.Model : "tencent/hy3:free";

        var newSession = await store.CreateAsync(Environment.CurrentDirectory, host.SessionModel.Agent, provider, model, ct).ConfigureAwait(false);
        if (newSession.IsFailure)
        {
            host.Bridge.AppendSystemLine($"! {newSession.Error}");
            host.WakeUp();
            return;
        }

        var agentDef = host.Services.GetRequiredService<IAgentRegistry>()
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
                SessionId = newSession.Value.Id,
                SessionTitle = newSession.Value.Title,
                Model = $"{provider}/{model}",
                Agent = newSession.Value.Agent,
                MessageCount = 0,
            };
        }

        await host.SyncSessionsToStoreAsync(ct).ConfigureAwait(false);
        host.Bridge.AppendSystemLine($"✓ Started fresh session: {newSession.Value.Id[..Math.Min(8, newSession.Value.Id.Length)]}");
        host.WakeUp();
    }
}
