// Issue #3629: the command-palette policies live in one BCL-only place so the
// desktop MVVM palette and the CellForge terminal palette drive the same
// behavior. This file owns no dispatcher, ObservableCollection or control, so
// both front ends can reach it without pulling in Avalonia or WPF.

using System;
using System.Collections.Generic;

namespace Harbor.Ui.Framework.ViewModels;

/// <summary>
/// Shared command-palette policies: prefix-scoped query history (#2682 Z01),
/// invocation ranking (Z02) and exact-query aliases (Z03). The weighted score
/// from #2681 G85 also lives here; the caller supplies the fuzzy scorer, so
/// the terminal palette keeps its engine-local <c>FuzzyMatcher</c> while both
/// palettes apply the same field weights and ordering policy.
/// </summary>
public sealed class CommandPalettePolicies
{
    private const double LabelWeight = 1.0;
    private const double HintWeight = 0.5;
    private const double KindWeight = 0.3;

    private readonly QueryHistory _history = new();
    private readonly Dictionary<string, int> _invocations = new(StringComparer.Ordinal);

    /// <summary>Queries committed this session, oldest first.</summary>
    public IReadOnlyList<string> RecentQueries => _history.Entries;

    /// <summary>Exact-query alias table. Keys are case-insensitive.</summary>
    public Dictionary<string, string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drop an in-flight history walk after a manual query edit (Z10).</summary>
    public void ResetHistoryNavigation() => _history.ResetNavigation();

    /// <summary>
    /// Walk prefix-matching history. Returns false and leaves
    /// <paramref name="nextQuery"/> unchanged when there is no entry to apply.
    /// </summary>
    public bool TryNavigateHistory(bool up, string currentQuery, out string nextQuery)
    {
        string? next = up ? _history.MoveBack(currentQuery) : _history.MoveForward(currentQuery);
        nextQuery = next ?? currentQuery;
        return next is not null;
    }

    /// <summary>
    /// Trim, lower-case and resolve an exact alias before matching. The
    /// rewrite is one step only, so a bad settings entry cannot form a cycle.
    /// </summary>
    public string ResolveQuery(string query)
    {
        string q = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (q.Length > 0 && Aliases.TryGetValue(q, out string? canonical))
        {
            q = canonical.Trim().ToLowerInvariant();
        }

        return q;
    }

    /// <summary>Record a committed query for later Up/Down navigation (Z01).</summary>
    public void NoteQuery(string query) => _history.Add(query);

    /// <summary>How often a row was invoked this session (Z02).</summary>
    public int InvocationCount(string kind, string label) =>
        _invocations.TryGetValue(RankKey(kind, label), out int count) ? count : 0;

    /// <summary>Record a successful invocation. Failed actions must not call this.</summary>
    public void NoteInvocation(string kind, string label)
    {
        string key = RankKey(kind, label);
        _invocations[key] = _invocations.TryGetValue(key, out int count) ? count + 1 : 1;
    }

    /// <summary>Stable identity of a row for usage counting, independent of display text changes.</summary>
    public static string RankKey(string kind, string label) => string.Concat(kind, ":", label);

    /// <summary>
    /// Best weighted score across the fields a palette row exposes. A field
    /// that does not match stays at -1, so a weak hint cannot outrank a
    /// non-matching label; each matching field keeps its own weight.
    /// </summary>
    /// <param name="score">Fuzzy scorer taking (text, query) and returning -1 for no match.</param>
    /// <param name="label">Primary search text of the row.</param>
    /// <param name="hint">Secondary search text of the row.</param>
    /// <param name="kind">Category of the row.</param>
    /// <param name="query">Lower-cased filter text.</param>
    public static double WeightedScore(
        Func<string, string, int> score,
        string label,
        string hint,
        string kind,
        string query) =>
        Math.Max(
            FieldScore(score, label, query, LabelWeight),
            Math.Max(FieldScore(score, hint, query, HintWeight), FieldScore(score, kind, query, KindWeight)));

    private static double FieldScore(Func<string, string, int> score, string text, string query, double weight)
    {
        int raw = score(text.ToLowerInvariant(), query);
        return raw < 0 ? -1d : raw * weight;
    }
}
