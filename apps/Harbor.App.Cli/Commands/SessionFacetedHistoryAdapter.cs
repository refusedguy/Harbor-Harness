using System.Globalization;
using Harbor.Abstractions.Models;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Bridges the CLI filter language to the domain-free
///     <see cref="FacetedHistoryState"/>. The primitive compares facet values
///     ordinally, while the CLI filter is case-insensitive and substring based.
///     This adapter normalises both sides to lower case and attaches a facet to
///     a session only when that session satisfies the token. The primitive then
///     ANDs the per-token constraints. This reproduces the old criterion-list
///     behaviour without a view.
/// </summary>
internal static class SessionFacetedHistoryAdapter
{
    /// <summary>Build a state whose <see cref="FacetedHistoryState.Visible"/> is the filtered session order.</summary>
    internal static FacetedHistoryState Build(
        IReadOnlyList<Session> sessions,
        SessionSearchFilter filter)
    {
        var state = new FacetedHistoryState();
        IReadOnlyList<SessionFilterToken> tokens = filter.Tokens;

        // The primitive's free-text query is already a case-insensitive
        // substring over HistoryEntry.Text, which is the session title. One
        // title/label token therefore becomes the query; extra tokens of the
        // same key stay facets so a repeated key keeps its AND meaning.
        int querySlot = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Key is "title" or "label")
            {
                querySlot = i;
                break;
            }
        }

        var entries = new List<HistoryEntry>(sessions.Count);
        for (int i = 0; i < sessions.Count; i++)
        {
            Session session = sessions[i];
            var facets = new List<HistoryFacet>(tokens.Count);
            for (int t = 0; t < tokens.Count; t++)
            {
                if (t == querySlot || !TokenHolds(session, tokens[t]))
                    continue;

                facets.Add(new HistoryFacet(SlotKey(t), tokens[t].Value));
            }

            // Detail stays empty. The query must only match the title, never a
            // directory that happens to contain the title filter value.
            entries.Add(new HistoryEntry(session.Id, session.Title, facets));
        }

        var selections = new List<FacetSelection>(tokens.Count);
        for (int t = 0; t < tokens.Count; t++)
        {
            if (t == querySlot)
                continue;

            selections.Add(new FacetSelection(SlotKey(t), tokens[t].Value));
        }

        state.Replace(entries);
        state.ReplaceFacets(selections);
        if (querySlot >= 0)
            state.SetQuery(tokens[querySlot].Value);

        return state;
    }

    // A token's slot is its facet key. Distinct slots make every token an
    // independent AND constraint, including repeated keys, which is how the
    // old criterion list combined tokens.
    private static string SlotKey(int slot) => slot.ToString(CultureInfo.InvariantCulture);

    private static bool TokenHolds(Session session, SessionFilterToken token) => token.Key switch
    {
        "id" or "session" => Contains(session.Id, token.Value),
        "project" => Contains(session.ProjectId, token.Value),
        "repo" or "dir" => Contains(session.Directory, token.Value),
        "parent" => session.ParentSessionId is not null && Contains(session.ParentSessionId, token.Value),
        "agent" => Contains(session.Agent, token.Value),
        "model" => Contains(session.Model, token.Value),
        "provider" => Contains(session.ProviderId, token.Value),
        "branch" => session.GitBranch is not null && Contains(session.GitBranch, token.Value),
        "status" => string.Equals(session.Status.ToString(), token.Value, StringComparison.OrdinalIgnoreCase),
        "kind" => string.Equals(session.Kind.ToString(), token.Value, StringComparison.OrdinalIgnoreCase),
        "after" => token.Date is not null && session.CreatedAt >= token.Date.Value,
        "before" => token.Date is not null && session.CreatedAt <= token.Date.Value,
        _ => false,
    };

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
