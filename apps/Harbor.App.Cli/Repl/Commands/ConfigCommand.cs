using System.Text;
using Harbor.Application.Configuration;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Config menu: view / set (validated input frame) / path.</summary>
internal sealed class ConfigCommand : IReplCommand
{
    public string Id => "config";
    public IReadOnlyList<string> Aliases => ["cfg"];
    public string Title => "Config";
    public string Description => "view or change runtime configuration";
    public string Group => "Config";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;

        host.Palette.PushFrame(new PaletteFrame(
            "Config", "config",
            new List<CommandItem>
            {
                new("view", "View Configuration", "Show current runtime configuration", string.Empty, "Actions"),
                new("set", "Set Option", "Change a configuration parameter", string.Empty, "Actions"),
                new("path", "Config Path", "Show filesystem location of config.json", string.Empty, "Actions")
            },
            OnCommitAsync: (item, frameCt) => HandleMenuAsync(host, item, frameCt)));
        host.WakeUp();
        return Task.CompletedTask;
    }

    private static async Task HandleMenuAsync(IReplHost host, CommandItem item, CancellationToken ct)
    {
        if (item.Id == "view")
        {
            await ViewConfigAsync(host, ct).ConfigureAwait(false);
            return;
        }

        if (item.Id == "path")
        {
            host.Bridge.AppendSystemLine($"Config path: {JsonConfigStore.GetDefaultPath()}");
            host.Palette.Hide();
            host.WakeUp();
            return;
        }

        if (item.Id == "set")
        {
            ShowSetList(host);
        }
    }

    private static async Task ViewConfigAsync(IReplHost host, CancellationToken ct)
    {
        var configStore = host.Services.GetRequiredService<IConfigStore>();
        var configResult = await configStore.LoadAsync(ct).ConfigureAwait(false);
        if (configResult.IsSuccess)
        {
            var c = configResult.Value;
            var sb = new StringBuilder();
            sb.AppendLine("Current configuration:");
            sb.AppendLine($"  model: {c.Model}");
            sb.AppendLine($"  provider: {c.Provider}");
            sb.AppendLine($"  agent: {c.Agent}");
            sb.AppendLine($"  tui: {c.Tui}");
            sb.AppendLine($"  storage: {c.Storage}");
            sb.AppendLine($"  maxSteps: {c.MaxSteps}");
            sb.AppendLine($"  costLimit: {c.CostLimit}");
            host.Bridge.AppendSystemLine(sb.ToString());
        }
        else
        {
            host.Bridge.AppendSystemLine($"! {configResult.Error}");
        }
        host.Palette.Hide();
        host.WakeUp();
    }

    private static void ShowSetList(IReplHost host)
    {
        var keyItems = new List<CommandItem>
        {
            new("model", "Model", "LLM model id", string.Empty, "Keys"),
            new("provider", "Provider", "LLM provider id", string.Empty, "Keys"),
            new("agent", "Agent", "Agent name", string.Empty, "Keys"),
            new("tui", "Tui", "TUI renderer", string.Empty, "Keys"),
            new("storage", "Storage", "Storage backend", string.Empty, "Keys"),
            new("maxsteps", "MaxSteps", "Max steps per turn", string.Empty, "Keys"),
            new("costlimit", "CostLimit", "Cost limit per session", string.Empty, "Keys")
        };

        host.Palette.PushFrame(new PaletteFrame(
            "Set Option", "config / set", keyItems,
            OnCommitAsync: (keyItem, frameCt) => ShowValueInput(host, keyItem)));
        host.WakeUp();
    }

    private static Task ShowValueInput(IReplHost host, CommandItem keyItem)
    {
        host.Palette.PushFrame(new PaletteFrame(
            $"config / set / {keyItem.Id}", $"config / set / {keyItem.Id}",
            [],
            IsInput: true,
            InputPlaceholder: "new value...",
            OnInputSubmitAsync: (value, frameCt) => ApplyValueAsync(host, keyItem.Id, value, frameCt)));
        host.WakeUp();
        return Task.CompletedTask;
    }

    private static async Task ApplyValueAsync(IReplHost host, string keyId, string value, CancellationToken ct)
    {
        // ROP: validate before touching the store — Parse inside UpdateAsync
        // would throw out instead of returning Result.
        int parsedMaxSteps = 0;
        decimal parsedCostLimit = 0;
        if (keyId == "maxsteps" && !int.TryParse(value, out parsedMaxSteps))
        {
            host.Bridge.AppendSystemLine($"✗ Invalid MaxSteps value: '{value}' (expected integer)");
            host.Palette.Hide();
            host.WakeUp();
            return;
        }

        if (keyId == "costlimit" && !decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out parsedCostLimit))
        {
            host.Bridge.AppendSystemLine($"✗ Invalid CostLimit value: '{value}' (expected decimal)");
            host.Palette.Hide();
            host.WakeUp();
            return;
        }

        var configStore = host.Services.GetRequiredService<IConfigStore>();
        var updateResult = await configStore.UpdateAsync(c =>
        {
            switch (keyId)
            {
                case "model": c.Model = value; break;
                case "provider": c.Provider = value; break;
                case "agent": c.Agent = value; break;
                case "tui": c.Tui = value; break;
                case "storage": c.Storage = value; break;
                case "maxsteps": c.MaxSteps = parsedMaxSteps; break;
                case "costlimit": c.CostLimit = parsedCostLimit; break;
            }
            return c;
        }, ct).ConfigureAwait(false);

        host.Bridge.AppendSystemLine(updateResult.IsSuccess
            ? $"✓ Set {keyId} = {value}"
            : $"✗ Failed: {updateResult.Error}");
        host.Palette.Hide();
        host.WakeUp();
    }
}
