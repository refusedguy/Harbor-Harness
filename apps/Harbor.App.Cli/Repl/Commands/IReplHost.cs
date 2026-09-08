using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
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
    IServiceProvider Services { get; }
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

    void WakeUp();
    void OpenSlashPalette();
    void ToggleVimMode();
    void ScrollTimelineToEnd();

    Task SwitchToSessionAsync(string sessionId, CancellationToken ct);
    Task ExecutePaletteItemAsync(CommandItem item, CancellationToken ct);
    Task ExecuteInfoAsync(string text, CancellationToken ct);
    Task RefreshSidebarSessionsAsync(CancellationToken ct);
}
