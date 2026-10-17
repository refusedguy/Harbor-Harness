using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Ui.Framework.Overlays;

/// <summary>
///     Session-list scope switch (opencode <c>dialog-session-list.tsx</c> parity,
///     <c>allProjects/cwd</c>). Persisted by the host under
///     <see cref="SessionListSearchModel.ScopePersistKey" />.
/// </summary>
public enum SessionListScope
{
    /// <summary>List sessions from every project.</summary>
    AllProjects = 0,

    /// <summary>List only sessions rooted at the current working directory.</summary>
    Cwd = 1,
}

/// <summary>
///     One session-list row for the two-tier search model: identity + grouping
///     hint + pin flag. BCL-pure so the filter/scope/rename/delete rules are
///     unit-testable without any session infrastructure.
/// </summary>
/// <param name="SessionId">Stable session id.</param>
/// <param name="Title">Human-readable session title.</param>
/// <param name="Directory">Absolute working directory (scope grouping hint).</param>
/// <param name="IsPinned">Pinned rows float to the top of the local tier.</param>
/// <param name="UpdatedAt">UTC last-activity time (latest-fallback ordering).</param>
public sealed record SessionListRow(
    string SessionId,
    string Title,
    string Directory,
    bool IsPinned,
    DateTimeOffset UpdatedAt);

/// <summary>
///     Pure two-tier session-list search (opencode <c>dialog-session-list.tsx</c>
///     steal, #1178): instant local filter + 150 ms-debounced server search with
///     latest fallback, <c>allProjects/cwd</c> scope switch over a persistent
///     storage key, and inline pin/rename/delete. Tier 1 (<see cref="FilterLocal" />)
///     answers every keystroke synchronously; tier 2 arrives debounced from the
///     host and wins via <see cref="CombineTiered" /> once
///     <see cref="IsDebounceDue" /> fires.
/// </summary>
public static class SessionListSearchModel
{
    /// <summary>Server-search debounce interval (opencode parity: 150 ms).</summary>
    public const int ServerDebounceMs = 150;

    /// <summary>
    ///     Persistent-storage key for the <see cref="SessionListScope" /> switch
    ///     (<c>allProjects/cwd</c> survives restarts).
    /// </summary>
    public const string ScopePersistKey = "session-list.scope";

    /// <summary>Max title length accepted by inline rename.</summary>
    public const int MaxTitleLength = 120;

    /// <summary>
    ///     Instant local tier: substring filter over title + id
    ///     (<see cref="StringComparison.OrdinalIgnoreCase" />), pinned rows
    ///     first (stable), empty query returns everything pinned-first.
    /// </summary>
    public static IReadOnlyList<SessionListRow> FilterLocal(
        IReadOnlyList<SessionListRow> rows, string? query)
    {
        Ensure.NotNull(rows);
        string q = query?.Trim() ?? string.Empty;

        var pinned = new List<SessionListRow>(rows.Count);
        var rest = new List<SessionListRow>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (q.Length != 0
                && !row.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !row.SessionId.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (row.IsPinned)
            {
                pinned.Add(row);
            }
            else
            {
                rest.Add(row);
            }
        }

        pinned.AddRange(rest);
        return pinned;
    }

    /// <summary>
    ///     Scope tier: <see cref="SessionListScope.AllProjects" /> (or an empty
    ///     <paramref name="cwd" />) keeps every row; <see cref="SessionListScope.Cwd" />
    ///     keeps rows rooted at <paramref name="cwd" /> (trailing separators ignored).
    /// </summary>
    public static IReadOnlyList<SessionListRow> ApplyScope(
        IReadOnlyList<SessionListRow> rows, SessionListScope scope, string? cwd)
    {
        Ensure.NotNull(rows);
        if (scope == SessionListScope.AllProjects || string.IsNullOrWhiteSpace(cwd))
        {
            return new List<SessionListRow>(rows);
        }

        string root = cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var kept = new List<SessionListRow>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            string dir = rows[i].Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(dir, root, StringComparison.Ordinal))
            {
                kept.Add(rows[i]);
            }
        }

        return kept;
    }

    /// <summary>
    ///     Two-tier merge with latest fallback: while the debounced server tier
    ///     is pending (<paramref name="serverReady" /> false) the instant local
    ///     tier paints; once ready, server hits win, and an empty server answer
    ///     falls back to <paramref name="latest" /> so the list never blinks empty.
    /// </summary>
    public static IReadOnlyList<SessionListRow> CombineTiered(
        IReadOnlyList<SessionListRow> local,
        IReadOnlyList<SessionListRow> server,
        IReadOnlyList<SessionListRow> latest,
        bool serverReady)
    {
        Ensure.NotNull(local);
        Ensure.NotNull(server);
        Ensure.NotNull(latest);
        if (!serverReady)
        {
            return local;
        }

        return server.Count > 0 ? server : latest;
    }

    /// <summary>
    ///     Whether the server tier is due: at least <see cref="ServerDebounceMs" />
    ///     elapsed since <paramref name="lastKeystrokeAt" />. Pure over explicit
    ///     timestamps so tests never sleep.
    /// </summary>
    public static bool IsDebounceDue(DateTimeOffset lastKeystrokeAt, DateTimeOffset now) =>
        (now - lastKeystrokeAt).TotalMilliseconds >= ServerDebounceMs;

    /// <summary>Inline pin toggle: flips <c>IsPinned</c> on the matching row (pure copy).</summary>
    public static IReadOnlyList<SessionListRow> TogglePin(
        IReadOnlyList<SessionListRow> rows, string? sessionId)
    {
        Ensure.NotNull(rows);
        var next = new List<SessionListRow>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            next.Add(string.Equals(row.SessionId, sessionId, StringComparison.Ordinal)
                ? row with { IsPinned = !row.IsPinned }
                : row);
        }

        return next;
    }

    /// <summary>
    ///     Inline rename: blank or over-<see cref="MaxTitleLength" /> titles fail
    ///     (list returned unchanged), otherwise the row carries the trimmed title.
    /// </summary>
    public static bool TryRename(
        IReadOnlyList<SessionListRow> rows,
        string? sessionId,
        string? newTitle,
        out IReadOnlyList<SessionListRow> updated)
    {
        Ensure.NotNull(rows);
        string title = newTitle?.Trim() ?? string.Empty;
        if (title.Length == 0 || title.Length > MaxTitleLength)
        {
            updated = rows;
            return false;
        }

        var next = new List<SessionListRow>(rows.Count);
        bool found = false;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (string.Equals(row.SessionId, sessionId, StringComparison.Ordinal))
            {
                found = true;
                next.Add(row with { Title = title });
            }
            else
            {
                next.Add(row);
            }
        }

        updated = next;
        return found;
    }

    /// <summary>Inline delete: drops the matching row; false (unchanged) when absent.</summary>
    public static bool Delete(
        IReadOnlyList<SessionListRow> rows,
        string? sessionId,
        out IReadOnlyList<SessionListRow> updated)
    {
        Ensure.NotNull(rows);
        var next = new List<SessionListRow>(rows.Count);
        bool found = false;
        for (int i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].SessionId, sessionId, StringComparison.Ordinal))
            {
                found = true;
                continue;
            }

            next.Add(rows[i]);
        }

        updated = next;
        return found;
    }
}
