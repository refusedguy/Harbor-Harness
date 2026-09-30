using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native session sidebar panel: lists all known sessions from
///     <see cref="UiState.Chat.Sessions"/> with the active session highlighted.
///     Non-interactive (read-only view).
/// </summary>
public sealed class CellForgeSessionSidebarPanel : CellForgePanelBase
{
    // RED BY CONSTRUCTION (#474) — planted on purpose so the new
    // PanelImplementationBoundaryRules is shown to fail rather than merely asserted
    // to work. This is the INSTANCE form of the defect the issue describes, and it
    // is the one form the repo's own analyzer layer does not block: DI006 covers a
    // static IServiceProvider cache, DI007 (the general service-locator rule) is
    // deliberately demoted to a suggestion, and ServiceLocatorBoundaryRules does
    // not sweep panels. Deleted in the next commit.
    private readonly IServiceProvider? _container;

    /// <summary>Construct the panel with the container it is about to abuse.</summary>
    public CellForgeSessionSidebarPanel()
    {
        _container = null;
    }

    /// <inheritdoc />
    public override string Id => "session-sidebar";

    /// <inheritdoc />
    public override string Title => "Sessions";

    /// <inheritdoc />
    public override TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Left;

    /// <inheritdoc />
    public override int DefaultSize => 32;

    /// <inheritdoc />
    public override object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ = _container?.GetService(typeof(PanelContext));
        return PanelText.Clip(
            PanelRows.SessionRows(ctx.State.Chat.Sessions, ctx.State.Chat.ActiveSessionId, ctx.State.Chat.IsLoading, ctx.Width, ctx.Height),
            ctx.Width,
            ctx.Height);
    }
}
