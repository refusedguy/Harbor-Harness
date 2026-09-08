using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Configuration;
using Harbor.Hosting.Rendering;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.App.Cli.Repl.Commands;

// TODO(principles)[DIP, ISP]: narrow further — Screen/Composer/Timeline are
// only needed by NewSession; next step is a dedicated INewSessionHost.
internal interface IReplHost
{
    IAgent Agent { get; }
    Session SessionModel { get; set; }
    ChatScreenBridge Bridge { get; }
    UiStore Store { get; }
    CommandPaletteView Palette { get; }
    StatusViewModel Status { get; }
    ChatScreen Screen { get; }
    SelectionEngine Selection { get; }
    VirtualizedChatTimeline Timeline { get; }
    ComposerController Composer { get; }

    // Typed dependencies (DIP: commands never touch IServiceProvider —
    // the host is composed at the root, lookup happens once here).
    IConfigStore ConfigStore { get; }
    IProviderRegistry ProviderRegistry { get; }
    IAgentRegistry AgentRegistry { get; }
    AuthStore AuthStore { get; }
    ISessionStore? SessionStore { get; }
    IRendererPipeline? RendererPipeline { get; }

    void WakeUp();
    void OpenSlashPalette();
    void ToggleVimMode();
    void ScrollTimelineToEnd();

    Task SwitchToSessionAsync(string sessionId, CancellationToken ct);
    Task ExecutePaletteItemAsync(CommandItem item, CancellationToken ct);
    Task ExecuteInfoAsync(string text, CancellationToken ct);
    Task SyncSessionsToStoreAsync(CancellationToken ct);
    Task<int> ResolveContextWindowAsync(string providerId, string modelId, CancellationToken ct);
}
