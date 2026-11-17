using System.Collections.Immutable;
using Harbor.Ui.Framework.Overlays;
using Ensure = Harbor.Abstractions.Extensions.Ensure;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native session sidebar panel: lists all known sessions from
///     <see cref="UiState.Chat.Sessions"/> with the active session highlighted.
///     Interactive since #1483: pin, inline rename, a two-step delete confirm
///     and the allProjects/cwd scope switch are folded by
///     <see cref="SessionSidebarState"/>, which in turn delegates every list
///     mutation to <see cref="SessionListSearchModel"/>. The panel holds no
///     filter, rename or delete rule of its own.
/// </summary>
/// <remarks>
///     The interaction state is provider-local (guarded by <c>_gate</c>) rather
///     than a new store axis, matching the file-tree and subagents panels: a
///     plugin can still construct this panel with no arguments (#474), and no
///     new extension axis or registry is introduced (#555). Search debounce and
///     the scope persistence key are passthroughs of the pure model so host and
///     model cannot drift apart.
/// </remarks>
public sealed class CellForgeSessionSidebarPanel : CellForgePanelBase
{
    private const string PinGlyph = "*";
    private const string ArmedGlyph = "!";

    private readonly object _gate = new();
    private SessionSidebarState _state = SessionSidebarState.Empty;
    private ImmutableArray<SessionInfo> _lastSessions;

    /// <inheritdoc />
    public override string Id => "session-sidebar";

    /// <inheritdoc />
    public override string Title => "Sessions";

    /// <inheritdoc />
    public override TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Left;

    /// <inheritdoc />
    public override int DefaultSize => 32;

    /// <summary>
    ///     Server-search debounce (ms) for the session-list dialog. Passthrough
    ///     of <see cref="SessionListSearchModel.ServerDebounceMs"/> so host
    ///     wiring and the pure model can never drift apart.
    /// </summary>
    internal static int SearchDebounceMs => SessionListSearchModel.ServerDebounceMs;

    /// <summary>Persistent scope key. Passthrough of the model key (issue #1483).</summary>
    internal static string ScopePersistKey => SessionSidebarState.ScopeKey;

    /// <inheritdoc />
    public override object? Build(PanelContext ctx)
    {
        Ensure.NotNull(ctx);
        var sessions = ctx.State.Chat.Sessions;

        lock (_gate)
        {
            if (!_lastSessions.Equals(sessions))
            {
                _state = _state.MergeSessions(ToRows(ctx, sessions));
                _lastSessions = sessions;
            }

            string cwd = ctx.State.Ui.ResolvePanelDirectory(Id);
            var visible = _state.VisibleRows(cwd);
            _state = _state.Move(0, visible.Count);
            return PanelText.Clip(RenderRows(ctx, visible), ctx.Width, ctx.Height);
        }
    }

    /// <inheritdoc />
    public override bool OnKey(UiKey key, PanelContext ctx)
    {
        Ensure.NotNull(ctx);
        if (key.Mods.IsCommandModifier())
        {
            return false;
        }

        lock (_gate)
        {
            string cwd = ctx.State.Ui.ResolvePanelDirectory(Id);
            var visible = _state.VisibleRows(cwd);
            string? id = SessionSidebarState.SelectedId(visible, _state.Cursor);
            bool editing = _state.RenameDraft is not null;

            switch (key.Code)
            {
                case UiKeyCode.Up:
                    _state = _state.Move(-1, visible.Count);
                    return true;
                case UiKeyCode.Down:
                    _state = _state.Move(1, visible.Count);
                    return true;
                case UiKeyCode.Backspace:
                    _state = editing ? _state.RenameBackspace() : _state.SetQuery(Backspace(_state.Query));
                    return true;
                case UiKeyCode.Escape:
                    if (editing || _state.DeleteArmedId is not null)
                    {
                        _state = _state.CancelInteraction();
                    }
                    else
                    {
                        _state = _state.SetQuery(string.Empty);
                    }

                    return true;
                case UiKeyCode.Enter:
                    if (!editing)
                    {
                        return false;
                    }

                    _state.CommitRename(id, out var renamed);
                    _state = renamed;
                    return true;
                case UiKeyCode.Char when !key.Mods.AcceptsTypedChar():
                    return false;
                case UiKeyCode.Char when (key.Character is 'p' or 'P') && !editing:
                    _state = _state.TogglePin(id, cwd);
                    return true;
                case UiKeyCode.Char when (key.Character is 'r' or 'R') && !editing:
                    _state = _state.BeginRename(CurrentTitle(visible, id));
                    return true;
                case UiKeyCode.Char when (key.Character is 'd' or 'D') && !editing:
                    if (_state.IsDeleteArmed(id))
                    {
                        _state.ConfirmDelete(id, out var deleted);
                        _state = deleted;
                    }
                    else
                    {
                        _state = _state.ArmDelete(id);
                    }

                    return true;
                case UiKeyCode.Char when (key.Character is 's' or 'S') && !editing:
                    _state = _state.ToggleScope();
                    return true;
                case UiKeyCode.Char when key.Character is not null:
                    _state = editing
                        ? _state.RenameType(key.Character.Value)
                        : _state.SetQuery(_state.Query + key.Character.Value);
                    return true;
                default:
                    return false;
            }
        }
    }

    private static string Backspace(string value) => value.Length == 0 ? string.Empty : value[..^1];

    private static string? CurrentTitle(IReadOnlyList<SessionListRow> visible, string? id)
    {
        if (id is null)
        {
            return null;
        }

        for (int i = 0; i < visible.Count; i++)
        {
            if (string.Equals(visible[i].SessionId, id, StringComparison.Ordinal))
            {
                return visible[i].Title;
            }
        }

        return null;
    }

    private static IReadOnlyList<SessionListRow> ToRows(
        PanelContext ctx, IReadOnlyList<SessionInfo> sessions)
    {
        var rows = new List<SessionListRow>(sessions.Count);
        var dirs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tab in ctx.State.Chat.TabStrip.Tabs)
        {
            dirs[tab.SessionId.Value] = tab.WorkingDirectory;
        }

        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            string id = session.SessionId.Value;
            rows.Add(new SessionListRow(
                id,
                session.Title,
                dirs.TryGetValue(id, out string? dir) ? dir : string.Empty,
                false,
                session.LastActivityAt));
        }

        return rows;
    }

    private List<string> RenderRows(PanelContext ctx, IReadOnlyList<SessionListRow> visible)
    {
        var rows = new List<string>(visible.Count + 5);
        rows.Add("Sessions");
        rows.Add(PanelText.Separator);
        if (visible.Count == 0)
        {
            rows.Add("(no sessions)");
        }
        else
        {
            int maxVisible = Math.Max(2, ctx.Height - 5);
            int end = Math.Min(visible.Count, maxVisible);
            for (int i = 0; i < end; i++)
            {
                var row = visible[i];
                string cursor = i == _state.Cursor ? "▸" : " ";
                string pin = row.IsPinned ? PinGlyph + " " : string.Empty;
                string armed = _state.IsDeleteArmed(row.SessionId) ? ArmedGlyph : string.Empty;
                string title = _state.RenameDraft is not null && i == _state.Cursor
                    ? _state.RenameDraft + "|"
                    : row.Title;
                rows.Add($"{cursor} {pin}{PanelText.Truncate(title, Math.Max(1, ctx.Width - 5))}{armed}");
            }
        }

        rows.Add(PanelText.Separator);
        string scope = _state.Scope == SessionListScope.Cwd ? "cwd" : "all";
        rows.Add($"scope:{scope} · p pin · r rename · d delete · Esc cancel");
        return rows;
    }
}
