using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Git;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Application.Configuration;
using Harbor.Hosting.Rendering;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Ui.Framework.State;
using System.Linq;
using Harbor.Registries.Agents;
using Harbor.Registries.Providers;
using TUnit.Assertions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Issue #3632: the REPL config palette shows humanized command/key names
///     while keeping raw ids for dispatch. "MaxSteps" becomes "Max Steps" in the
///     set-key palette, but the item id remains the config key.
/// </summary>
public class ConfigCommandPaletteHumanizationTests
{
    private sealed class FakeHost : IReplHost
    {
        public IAgent Agent => null!;
        public Session SessionModel { get => null!; set { } }
        public ChatScreenBridge Bridge => null!;
        public UiStore Store => null!;
        public CommandPaletteView Palette { get; } = new();
        public StatusViewModel Status => null!;
        public ChatScreen Screen => null!;
        public SelectionEngine Selection => null!;
        public VirtualizedChatTimeline Timeline => null!;
        public ComposerController Composer => null!;
        public IConfigStore ConfigStore => null!;
        public IProviderRegistry ProviderRegistry => null!;
        public IAgentRegistry AgentRegistry => null!;
        public AuthStore AuthStore => null!;
        public ISessionStore? SessionStore => null;
        public IRendererPipeline? RendererPipeline => null;
        public IPanelRegistry? PanelRegistry => null;
        public Harbor.App.Cli.Repl.ImageAttachmentStash? Attachments => null;
        public Harbor.Hosting.PluginReloadService? PluginReload => null;
        public IProviderHealthCheck? HealthCheck => null;
        public IGitQuery? Git => null;
        public void WakeUp() { }
        public void OpenSlashPalette() { }
        public void ToggleVimMode() { }
        public void ScrollTimelineToEnd() { }
        public void RequestQuit(int exitCode) { }
        public Task SwitchToSessionAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
        public Task ExecutePaletteItemAsync(CommandItem item, CancellationToken ct) => Task.CompletedTask;
        public Task ExecuteInfoAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public Task SyncSessionsToStoreAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<int> ResolveContextWindowAsync(string providerId, string modelId, CancellationToken ct) => Task.FromResult(0);
    }

    [Test]
    public async Task SetPalette_HumanizesKeyTitles_AndKeepsRawIds()
    {
        var host = new FakeHost();
        var command = new ConfigCommand();

        await command.ExecuteAsync(new ReplCommandContext(host, "config"), CancellationToken.None);

        // Menu frame: view / set / path. Select "Set Option" and commit it to
        // drill into the key list the same way the live palette does.
        var setRow = host.Palette.Results.Single(r => r.Id == "set");
        while (host.Palette.Results[host.Palette.SelectedIndex].Id != "set")
        {
            host.Palette.HandleKey(KeyEvent.Simple(KeyCode.Down));
        }

        await Assert.That(host.Palette.Results[host.Palette.SelectedIndex].Id).IsEqualTo("set");
        _ = host.Palette.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        var pending = host.Palette.TakePendingCommit();
        await Assert.That(pending).IsNotNull();
        await pending!.Value.Handler(pending.Value.Item, CancellationToken.None);

        var maxSteps = host.Palette.Results.Single(r => r.Id == "maxsteps");
        await Assert.That(maxSteps.Title).IsEqualTo("Max Steps");
        await Assert.That(maxSteps.Id).IsEqualTo("maxsteps");

        var costLimit = host.Palette.Results.Single(r => r.Id == "costlimit");
        await Assert.That(costLimit.Title).IsEqualTo("Cost Limit");
        await Assert.That(costLimit.Id).IsEqualTo("costlimit");
    }
}
