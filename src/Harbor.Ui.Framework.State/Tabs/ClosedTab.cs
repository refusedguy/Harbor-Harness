using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     One entry of the tab reopen stack (#1173, opencode steal): the closed
///     tab's descriptor plus the position it occupied, so
///     <c>Ctrl+Shift+T</c> restores it where it was, not at the end.
///     Mirrors <c>ClosedSessionTab = { tab, index }</c> from opencode's
///     <c>session-tabs-model.ts</c>.
/// </summary>
/// <param name="Tab">The descriptor as it was at close time (title, flags, panels).</param>
/// <param name="Index">Zero-based position in tab order at close time. Clamped on restore.</param>
public sealed record ClosedTab(SessionTab Tab, int Index)
{
    /// <summary>Session identity shortcut — the stack dedupes and looks up by this.</summary>
    public SessionId SessionId => Tab.SessionId;
}
