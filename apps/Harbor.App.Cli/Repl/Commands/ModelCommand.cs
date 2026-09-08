using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Application.Configuration;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Model switcher: provider/model list → config update → agent rebind.</summary>
internal sealed class ModelCommand : IReplCommand
{
    public string Id => "model";
    public IReadOnlyList<string> Aliases => ["m"];
    public string Title => "Model";
    public string Description => "switch LLM model";
    public string Group => "Runtime";

    public async Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;
        var providers = host.Services.GetRequiredService<IProviderRegistry>();
        var allModels = await providers.GetAllModelsAsync(ct).ConfigureAwait(false);
        if (allModels.IsFailure)
        {
            host.Bridge.AppendSystemLine($"! {allModels.Error}");
            host.WakeUp();
            return;
        }

        var items = new List<CommandItem>();
        foreach (var group in allModels.Value.GroupBy(m => m.ProviderId))
        {
            foreach (var m in group)
            {
                items.Add(new CommandItem(
                    m.Id,
                    m.Id,
                    m.DisplayName,
                    string.Empty,
                    group.Key));
            }
        }

        host.Palette.PushFrame(new PaletteFrame(
            "Select Model", "model", items,
            OnCommitAsync: (item, frameCt) => ApplyModelAsync(host, item, frameCt)));
        host.WakeUp();
    }

    private static async Task ApplyModelAsync(IReplHost host, CommandItem item, CancellationToken ct)
    {
        string providerId = !string.IsNullOrEmpty(item.Group) ? item.Group : host.SessionModel.ProviderId;
        string modelId = item.Id;
        string canonicalModel = $"{providerId}/{modelId}";

        var configStore = host.Services.GetRequiredService<IConfigStore>();
        var result = await configStore.UpdateAsync(c =>
        {
            c.Provider = providerId;
            c.Model = canonicalModel;
            return c;
        }, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            host.Bridge.AppendSystemLine($"✓ Model switched to {canonicalModel}");
            host.Status.Model = modelId;
            if (host.Screen.Sidebar is { } sb) sb.State = sb.State with { Model = canonicalModel };
            host.Selection.Clear();

            var agentDef = host.Services.GetRequiredService<IAgentRegistry>()
                .GetAgent(AgentName.Create(host.SessionModel.Agent));
            if (agentDef.IsSuccess)
            {
                host.SessionModel = host.SessionModel with { ProviderId = providerId, Model = modelId };
                host.Agent.Initialize(host.SessionModel, agentDef.Value.WithModel(modelId, providerId));
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
