using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Ui.Framework.Overlays;

/// <summary>
///     Interaction state for the session sidebar (issue #1483). Holds the
///     selection, the filter query, the allProjects/cwd scope, an inline rename
///     draft and the armed delete confirm. Every list mutation is delegated to
///     <see cref="SessionListSearchModel" />; this type owns no filter, rename or
///     delete rules of its own, so the panel cannot drift from the pure model.
/// </summary>
/// <remarks>
///     Immutable by construction: each transition returns a new record, which
///     keeps the state testable as a value and safe to hand to the render thread
///     while the input thread folds the next key. No new extension axis is
///     introduced: this is a plain value type beside the model it wraps.
/// </remarks>
public sealed record SessionSidebarState
{
    /// <summary>Initial state: no rows, unfiltered, all projects, no interaction open.</summary>
    public static SessionSidebarState Empty { get; } = new();

    /// <summary>Rows currently known to the sidebar, including local pin/rename edits.</summary>
    public IReadOnlyList<SessionListRow> Rows { get; init; } = Array.Empty<SessionListRow>();

    /// <summary>Instant local filter text.</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>Scope switch, persisted by the host under <see cref="SessionListSearchModel.ScopePersistKey" />.</summary>
    public SessionListScope Scope { get; init; } = SessionListScope.AllProjects;

    /// <summary>Index of the selected row within <see cref="VisibleRows" />.</summary>
    public int Cursor { get; init; }

    /// <summary>Non-null while inline rename is editing. Null means no rename is open.</summary>
    public string? RenameDraft { get; init; }

    /// <summary>Id of the row whose delete confirm is armed. Null when disarmed.</summary>
    public string? DeleteArmedId { get; init; }

    /// <summary>Persistent key for the scope switch. Single passthrough of the model key.</summary>
    public static string ScopeKey => SessionListSearchModel.ScopePersistKey;

    /// <summary>Rows the sidebar paints: scope first, then local filter (pinned first).</summary>
    public IReadOnlyList<SessionListRow> VisibleRows(string? cwd = null) =>
        SessionListSearchModel.FilterLocal(
            SessionListSearchModel.ApplyScope(Rows, Scope, cwd), Query);

    /// <summary>Id of the row at <paramref name="cursor" />, or null when out of range.</summary>
    public static string? SelectedId(IReadOnlyList<SessionListRow> visible, int cursor) =>
        cursor >= 0 && cursor < visible.Count ? visible[cursor].SessionId : null;

    /// <summary>Replace the filter text and reset the cursor. Closes any armed delete.</summary>
    public SessionSidebarState SetQuery(string? query) =>
        this with { Query = query ?? string.Empty, Cursor = 0, DeleteArmedId = null };

    /// <summary>Move the cursor by <paramref name="delta" /> rows, clamped to <paramref name="count" />.</summary>
    public SessionSidebarState Move(int delta, int count) =>
        this with { Cursor = Math.Clamp(Cursor + delta, 0, Math.Max(0, count - 1)) };

    /// <summary>
    ///     Reconcile a fresh host snapshot into the local rows. Edits (pin flag,
    ///     renamed title) survive for ids that are still present, so a refresh
    ///     does not silently undo the user's last action.
    /// </summary>
    public SessionSidebarState MergeSessions(IReadOnlyList<SessionListRow> fresh)
    {
        Ensure.NotNull(fresh);
        var previous = new Dictionary<string, SessionListRow>(StringComparer.Ordinal);
        for (int i = 0; i < Rows.Count; i++)
        {
            previous[Rows[i].SessionId] = Rows[i];
        }

        var merged = new List<SessionListRow>(fresh.Count);
        for (int i = 0; i < fresh.Count; i++)
        {
            var row = fresh[i];
            merged.Add(previous.TryGetValue(row.SessionId, out var old)
                ? row with { IsPinned = old.IsPinned, Title = old.Title }
                : row);
        }

        return this with
        {
            Rows = merged,
            Cursor = Math.Clamp(Cursor, 0, Math.Max(0, merged.Count - 1)),
        };
    }

    /// <summary>
    ///     Toggle the pinned flag of <paramref name="sessionId" /> through
    ///     <see cref="SessionListSearchModel.TogglePin" /> and keep the cursor on
    ///     the same row after the pinned-first re-sort.
    /// </summary>
    public SessionSidebarState TogglePin(string? sessionId, string? cwd = null)
    {
        var rows = SessionListSearchModel.TogglePin(Rows, sessionId);
        var visible = SessionListSearchModel.FilterLocal(
            SessionListSearchModel.ApplyScope(rows, Scope, cwd), Query);
        int next = -1;
        for (int i = 0; i < visible.Count; i++)
        {
            if (string.Equals(visible[i].SessionId, sessionId, StringComparison.Ordinal))
            {
                next = i;
                break;
            }
        }

        return this with { Rows = rows, Cursor = next < 0 ? 0 : next };
    }

    /// <summary>Open inline rename seeded with the row's current title.</summary>
    public SessionSidebarState BeginRename(string? currentTitle) =>
        this with { RenameDraft = currentTitle ?? string.Empty, DeleteArmedId = null };

    /// <summary>Append a typed character to the rename draft (no-op when no rename is open).</summary>
    public SessionSidebarState RenameType(char character) =>
        RenameDraft is null ? this : this with { RenameDraft = RenameDraft + character };

    /// <summary>Remove the last character of the rename draft.</summary>
    public SessionSidebarState RenameBackspace() =>
        RenameDraft is null
            ? this
            : this with { RenameDraft = RenameDraft.Length == 0 ? string.Empty : RenameDraft[..^1] };

    /// <summary>
    ///     Commit the rename through <see cref="SessionListSearchModel.TryRename" />.
    ///     Blank and over-long drafts are rejected by the model and leave the rows
    ///     unchanged. The draft always closes.
    /// </summary>
    public bool CommitRename(string? sessionId, out SessionSidebarState next)
    {
        if (RenameDraft is null || sessionId is null)
        {
            next = this with { RenameDraft = null };
            return false;
        }

        bool ok = SessionListSearchModel.TryRename(Rows, sessionId, RenameDraft, out var updated);
        next = ok
            ? this with { Rows = updated, RenameDraft = null }
            : this with { RenameDraft = null };
        return ok;
    }

    /// <summary>
    ///     First delete press arms the confirm for <paramref name="sessionId" />.
    ///     Pressing it again on the same id arms nothing (the confirm path calls
    ///     <see cref="ConfirmDelete" /> instead). Any other id moves the arm.
    /// </summary>
    public SessionSidebarState ArmDelete(string? sessionId) =>
        sessionId is null
            ? this with { DeleteArmedId = null }
            : this with
            {
                DeleteArmedId = string.Equals(DeleteArmedId, sessionId, StringComparison.Ordinal)
                    ? null
                    : sessionId,
                RenameDraft = null,
            };

    /// <summary>Whether the delete confirm is armed for this id.</summary>
    public bool IsDeleteArmed(string? sessionId) =>
        sessionId is not null && string.Equals(DeleteArmedId, sessionId, StringComparison.Ordinal);

    /// <summary>
    ///     Second delete press. Deletes only when this exact id was armed, then
    ///     disarms through <see cref="SessionListSearchModel.Delete" />. An Esc
    ///     cancel leaves no arm, so this cannot delete by accident.
    /// </summary>
    public bool ConfirmDelete(string? sessionId, out SessionSidebarState next)
    {
        if (!IsDeleteArmed(sessionId))
        {
            next = this with { DeleteArmedId = null };
            return false;
        }

        bool ok = SessionListSearchModel.Delete(Rows, sessionId, out var updated);
        next = ok
            ? this with
            {
                Rows = updated,
                DeleteArmedId = null,
                Cursor = Math.Clamp(Cursor, 0, Math.Max(0, updated.Count - 1)),
            }
            : this with { DeleteArmedId = null };
        return ok;
    }

    /// <summary>Esc: close rename and disarm delete with no side effect on rows.</summary>
    public SessionSidebarState CancelInteraction() =>
        this with { RenameDraft = null, DeleteArmedId = null };

    /// <summary>Flip allProjects/cwd. The host persists the new value under <see cref="ScopeKey" />.</summary>
    public SessionSidebarState ToggleScope() =>
        this with
        {
            Scope = Scope == SessionListScope.AllProjects
                ? SessionListScope.Cwd
                : SessionListScope.AllProjects,
            RenameDraft = null,
            DeleteArmedId = null,
        };
}
