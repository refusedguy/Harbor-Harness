using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Core of <c>harbor sessions tree</c> and the REPL <c>/tree</c> command:
///     renders the fork/branch lineage (via <see cref="Session.ParentSessionId" />)
///     as an indented forest. Read-only; ordering is deterministic
///     (<c>CreatedAt</c>, then <c>Id</c>).
/// </summary>
public static class SessionTreeRunner
{
    /// <summary>
    ///     Build the rendered tree lines. The current session (when known) is
    ///     marked with <c>(current)</c>; orphans (parent id not in the store)
    ///     are shown as roots with a note; parent-chain cycles are cut with a
    ///     <c>(cycle)</c> marker instead of recursing forever.
    /// </summary>
    /// <param name="store">Session backend to list.</param>
    /// <param name="currentSessionId">Id of the active session, if any.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<Result<IReadOnlyList<string>>> BuildAsync(
        ISessionStore store, string? currentSessionId = null, CancellationToken ct = default)
    {
        var listed = await store.ListAsync(ct: ct).ConfigureAwait(false);
        if (listed.IsFailure)
            return Result.Failure<IReadOnlyList<string>>(listed.Error);

        return Result.Success(RenderForest(listed.Value, currentSessionId));
    }

    /// <summary>
    ///     One-shot CLI face (<c>harbor sessions tree</c>): writes the tree to
    ///     <paramref name="output" />. Returns a process exit code.
    /// </summary>
    public static async Task<int> RunAsync(
        TextWriter output,
        TextWriter error,
        ISessionStore store,
        string? currentSessionId = null,
        CancellationToken ct = default)
    {
        var built = await BuildAsync(store, currentSessionId, ct).ConfigureAwait(false);
        if (built.IsFailure)
        {
            await error.WriteLineAsync($"Cannot list sessions: {built.Error}").ConfigureAwait(false);
            return 1;
        }

        if (built.Value.Count == 0)
            await output.WriteLineAsync("No sessions.").ConfigureAwait(false);
        else
            foreach (string line in built.Value)
                await output.WriteLineAsync(line).ConfigureAwait(false);
        return 0;
    }

    internal static IReadOnlyList<string> RenderForest(IReadOnlyList<Session> sessions, string? currentSessionId)
    {
        var byId = new Dictionary<string, Session>(sessions.Count, StringComparer.Ordinal);
        foreach (var s in sessions)
            byId[s.Id] = s;

        var children = new Dictionary<string, List<Session>>(StringComparer.Ordinal);
        var roots = new List<Session>();
        foreach (var s in sessions)
        {
            if (s.ParentSessionId is { } parent && byId.ContainsKey(parent))
            {
                if (!children.TryGetValue(parent, out var list))
                    children[parent] = list = [];
                list.Add(s);
            }
            else
            {
                roots.Add(s);
            }
        }

        SortByAge(roots);
        foreach (var list in children.Values)
            SortByAge(list);

        var lines = new List<string>();
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
            RenderNode(root, string.Empty, true, lines, children, byId, currentSessionId, onPath);
        return lines;
    }

    private static void SortByAge(List<Session> list) =>
        list.Sort(static (a, b) =>
        {
            int byTime = a.CreatedAt.CompareTo(b.CreatedAt);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Id, b.Id);
        });

    private static void RenderNode(
        Session node,
        string prefix,
        bool isLast,
        List<string> lines,
        Dictionary<string, List<Session>> children,
        Dictionary<string, Session> byId,
        string? currentSessionId,
        HashSet<string> onPath)
    {
        string title = string.IsNullOrWhiteSpace(node.Title) ? "(untitled)" : node.Title;
        string branch = prefix.Length == 0 ? "* " : prefix + (isLast ? "└─ " : "├─ ");
        string note = string.Empty;
        if (node.ParentSessionId is { } parent && !byId.ContainsKey(parent))
            note = $" (parent {parent} not found)";
        if (node.Id.Equals(currentSessionId, StringComparison.Ordinal))
            note += " (current)";
        lines.Add($"{branch}{node.Id} — {title} [{node.Agent}/{node.Model}]{note}");

        if (!onPath.Add(node.Id))
        {
            lines.Add($"{prefix}{(isLast ? "   " : "│  ")}└─ (cycle — parent chain loops)");
            return;
        }

        try
        {
            if (!children.TryGetValue(node.Id, out var kids))
                return;
            string childPrefix = prefix + (prefix.Length == 0 ? "   " : isLast ? "   " : "│  ");
            for (int i = 0; i < kids.Count; i++)
                RenderNode(kids[i], childPrefix, i == kids.Count - 1, lines, children, byId, currentSessionId, onPath);
        }
        finally
        {
            onPath.Remove(node.Id);
        }
    }
}
