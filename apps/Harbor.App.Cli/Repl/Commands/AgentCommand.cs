using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Application.Configuration;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Agent switcher: agent list → config update → agent re-init.</summary>
internal sealed class AgentCommand : IReplCommand
{
    public string Id => "agent";
    public IReadOnlyList<string> Aliases => ["a", "mode"];
    public string Title => "Agent";
    public string Description => "switch active agent";
    public string Group => "Runtime";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;
        var registry = host.Services.GetRequiredService<IAgentRegistry>();
        var items = registry.GetAllAgents()
            .Select(a => new CommandItem(
                a.Name.Value,
                a.Name.Value,
                a.Description,
                string.Empty,
                "Agents"))
            .ToList();

        host.Palette.PushFrame(new PaletteFrame(
            "Select Agent", "agent", items,
            OnCommitAsync: (item, frameCt) => ApplyAgentAsync(host, registry, item, frameCt)));
        host.WakeUp();
        return Task.CompletedTask;
    }

    private static async Task ApplyAgentAsync(IReplHost host, IAgentRegistry registry, CommandItem item, CancellationToken ct)
    {
        var configStore = host.Services.GetRequiredService<IConfigStore>();
        var result = await configStore.UpdateAsync(c =>
        {
            c.Agent = item.Id;
            return c;
        }, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            host.Bridge.AppendSystemLine($"✓ Switched to agent: {item.Id}");
            host.Selection.Clear();
            var agentDef = registry.GetAgent(AgentName.Create(item.Id));
            if (agentDef.IsSuccess)
            {
                host.Agent.Initialize(host.SessionModel, agentDef.Value);
                _ = host.Store.Dispatch(new UiMsg.ConfigureRuntime(host.SessionModel.Model, host.SessionModel.ProviderId, item.Id));
            }
        }
        else
        {
            host.Bridge.AppendSystemLine($"✗ Failed: {result.Error}");
        }
        host.Palette.Hide();
        host.WakeUp();
    }
}
