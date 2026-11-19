// #2706 P13: domain-free faceted history state. The primitive owns a prepared
// collection of entries, a text query, a set of facet selections and one cursor
// into the filtered result. It knows nothing about exit codes, cwd, hosts,
// durations or session resume: those are facets the caller supplies, and the
// selected entry is reported back as a value. BCL-only, AOT-clean, render
// thread owned — the same leaf rule as TwoPaneExplorer and BreadcrumbTrail.

using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>One named metadata value attached to a history entry.</summary>
/// <param name="Key">Facet name, for example "exit" or "host".</param>
/// <param name="Value">Facet value. The primitive compares values ordinally.</param>
public readonly record struct HistoryFacet(string Key, string Value);

/// <summary>
/// One prepared history record. The primitive only knows its identity, its
/// primary search text, an optional detail line and its facets. Any richer
/// payload stays with the caller and is looked up by <see cref="Id"/> when the
/// entry is selected.
/// </summary>
/// <param name="Id">Stable identity supplied by the caller.</param>
/// <param name="Text">Primary free-text search field.</param>
/// <param name="Facets">Metadata pairs used for faceted filtering.</param>
/// <param name="Detail">Optional detail line shown under the result list.</param>
public sealed record HistoryEntry(
    string Id,
    string Text,
    IReadOnlyList<HistoryFacet> Facets,
    string Detail = "");

/// <summary>One active facet constraint: keep entries carrying this key and value.</summary>
/// <param name="Key">Facet name.</param>
/// <param name="Value">Facet value to require.</param>
public readonly record struct FacetSelection(string Key, string Value);

/// <summary>A facet value and how many currently matching entries carry it.</summary>
/// <param name="Value">Facet value.</param>
/// <param name="Count">Number of matching entries with that value.</param>
public readonly record struct FacetCount(string Value, int Count);

/// <summary>One projected text row of the history view.</summary>
/// <param name="Text">Truncated single-line text.</param>
/// <param name="IsCursor">True for the row under the cursor.</param>
public readonly record struct HistoryRow(string Text, bool IsCursor);

/// <summary>
/// State for the faceted history pattern (atuin, fzf CTRL-R, k9s command mode):
/// a searchable list where the user narrows results by free text and by one or
/// more metadata facets, then acts on the selected record. The widget owns the
/// query, the facet selections and the cursor; the caller owns the entries.
/// Selecting an entry is a value returned to the host, not a mutation of a
/// domain model, so the primitive carries no application type on its surface.
/// </summary>
/// <remarks>
/// <para>
/// Facet constraints combine with AND across distinct keys and OR within one
/// key. Selecting "exit=ok" and "host=box" keeps entries carrying both; adding
/// a second value for "exit" widens that key to either value. This is the
/// behaviour faceted browsers use and it needs no knowledge of what a facet
/// means.
/// </para>
/// <para>
/// <see cref="Rows"/> is a pure projection of the state for a clip rectangle,
/// and <see cref="Paint"/> places those rows in a <see cref="ScreenBuffer"/>.
/// The same state and clip always produce the same rows.
/// </para>
/// </remarks>
public sealed class FacetedHistoryState
{
    private IReadOnlyList<HistoryEntry> _entries = Array.Empty<HistoryEntry>();
    private IReadOnlyList<FacetSelection> _facets = Array.Empty<FacetSelection>();
    private string _query = string.Empty;
    private int _cursor;

    /// <summary>All entries supplied by the caller, before filtering.</summary>
    public IReadOnlyList<HistoryEntry> Entries => _entries;

    /// <summary>Active facet constraints.</summary>
    public IReadOnlyList<FacetSelection> Facets => _facets;

    /// <summary>Current free-text query, case-insensitive.</summary>
    public string Query => _query;

    /// <summary>Cursor index into <see cref="Visible"/>.</summary>
    public int Cursor => _cursor;

    /// <summary>Number of entries before filtering.</summary>
    public int TotalCount => _entries.Count;

    /// <summary>Entries matching the query and every facet selection.</summary>
    public IReadOnlyList<HistoryEntry> Visible()
    {
        if (_entries.Count == 0)
        {
            return Array.Empty<HistoryEntry>();
        }

        var matches = new List<HistoryEntry>(_entries.Count);
        for (int i = 0; i < _entries.Count; i++)
        {
            if (Matches(_entries[i], _facets, _query))
            {
                matches.Add(_entries[i]);
            }
        }

        return matches;
    }

    /// <summary>Entry under the cursor, or null when the filtered list is empty.</summary>
    public HistoryEntry? Selected
    {
        get
        {
            IReadOnlyList<HistoryEntry> visible = Visible();
            if (visible.Count == 0)
            {
                return null;
            }

            return visible[Math.Clamp(_cursor, 0, visible.Count - 1)];
        }
    }

    /// <summary>Replaces the entry collection, clamping the cursor into range.</summary>
    /// <returns>True when the cursor was clamped.</returns>
    public bool Replace(IReadOnlyList<HistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _entries = entries;
        return ClampCursor();
    }

    /// <summary>
    /// Sets the free-text query. A changed query resets the cursor to the first
    /// result, because an index into the old result set has no meaning here.
    /// </summary>
    /// <returns>True when the query changed.</returns>
    public bool SetQuery(string? query)
    {
        string next = query ?? string.Empty;
        if (string.Equals(_query, next, StringComparison.Ordinal))
        {
            return false;
        }

        _query = next;
        _cursor = 0;
        return true;
    }

    /// <summary>Adds a facet constraint, or removes it when already active.</summary>
    /// <returns>True when the selection set changed.</returns>
    public bool ToggleFacet(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var next = new List<FacetSelection>(_facets.Count + 1);
        bool removed = false;
        for (int i = 0; i < _facets.Count; i++)
        {
            FacetSelection facet = _facets[i];
            if (string.Equals(facet.Key, key, StringComparison.Ordinal) &&
                string.Equals(facet.Value, value, StringComparison.Ordinal))
            {
                removed = true;
                continue;
            }

            next.Add(facet);
        }

        if (!removed)
        {
            next.Add(new FacetSelection(key, value));
        }

        _facets = next;
        _cursor = 0;
        return true;
    }

    /// <summary>Removes every facet constraint.</summary>
    /// <returns>True when constraints were cleared.</returns>
    public bool ClearFacets()
    {
        if (_facets.Count == 0)
        {
            return false;
        }

        _facets = Array.Empty<FacetSelection>();
        _cursor = 0;
        return true;
    }

    /// <summary>
    /// Replaces every facet constraint in one step. State-only consumers that
    /// parse a complete filter expression use this instead of toggling facets
    /// one at a time. The cursor returns to the first result because the old
    /// index has no meaning for the new constraint set.
    /// </summary>
    /// <returns>True when any constraint changed.</returns>
    public bool ReplaceFacets(IReadOnlyList<FacetSelection> facets)
    {
        ArgumentNullException.ThrowIfNull(facets);

        var next = new List<FacetSelection>(facets.Count);
        for (int i = 0; i < facets.Count; i++)
        {
            next.Add(facets[i]);
        }

        bool changed = next.Count != _facets.Count;
        if (!changed)
        {
            for (int i = 0; i < next.Count; i++)
            {
                if (!next[i].Equals(_facets[i]))
                {
                    changed = true;
                    break;
                }
            }
        }

        _facets = next;
        if (changed)
        {
            _cursor = 0;
        }

        return changed;
    }

    /// <summary>Sets the cursor, clamped to the visible result count.</summary>
    /// <returns>True when the cursor moved.</returns>
    public bool SetCursor(int index)
    {
        int limit = Math.Max(0, Visible().Count - 1);
        int next = Math.Clamp(index, 0, limit);
        if (next == _cursor)
        {
            return false;
        }

        _cursor = next;
        return true;
    }

    /// <summary>Moves the cursor by a signed delta, clamped to the result bounds.</summary>
    /// <returns>True when the cursor moved.</returns>
    public bool MoveCursor(int delta)
    {
        int limit = Math.Max(0, Visible().Count - 1);
        long next = (long)_cursor + delta;
        return SetCursor((int)Math.Clamp(next, 0, limit));
    }

    /// <summary>
    /// Counts the values of one facet among entries that match the query and
    /// every <em>other</em> facet key. Excluding the key itself keeps counts
    /// useful while the user drills into that key. Results are sorted by count
    /// descending, then by value ordinally, so the order is deterministic.
    /// </summary>
    public IReadOnlyList<FacetCount> FacetValues(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var others = new List<FacetSelection>(_facets.Count);
        for (int i = 0; i < _facets.Count; i++)
        {
            if (!string.Equals(_facets[i].Key, key, StringComparison.Ordinal))
            {
                others.Add(_facets[i]);
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < _entries.Count; i++)
        {
            HistoryEntry entry = _entries[i];
            if (!Matches(entry, others, _query))
            {
                continue;
            }

            for (int j = 0; j < entry.Facets.Count; j++)
            {
                HistoryFacet facet = entry.Facets[j];
                if (!string.Equals(facet.Key, key, StringComparison.Ordinal))
                {
                    continue;
                }

                counts.TryGetValue(facet.Value, out int current);
                counts[facet.Value] = current + 1;
            }
        }

        var result = new List<FacetCount>(counts.Count);
        foreach (KeyValuePair<string, int> pair in counts)
        {
            result.Add(new FacetCount(pair.Key, pair.Value));
        }

        result.Sort(static (a, b) =>
        {
            int byCount = b.Count.CompareTo(a.Count);
            return byCount != 0 ? byCount : string.CompareOrdinal(a.Value, b.Value);
        });
        return result;
    }

    /// <summary>
    /// Pure row projection: a header with visible and total counts, a facet
    /// line, a cursor-centred window of results, and the selected detail line.
    /// </summary>
    public IReadOnlyList<HistoryRow> Rows(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return Array.Empty<HistoryRow>();
        }

        IReadOnlyList<HistoryEntry> visible = Visible();
        var rows = new List<HistoryRow>(height)
        {
            new HistoryRow(
                UnicodeTruncation.Truncate($"History ({visible.Count}/{_entries.Count})", width, string.Empty),
                false),
        };

        if (height == 1)
        {
            return rows;
        }

        rows.Add(new HistoryRow(
            UnicodeTruncation.Truncate(FacetLine(), width, UnicodeTruncation.Ellipsis),
            false));

        bool hasDetail = height >= 4;
        int bodyRows = Math.Max(0, height - (hasDetail ? 3 : 2));
        if (bodyRows > 0 && visible.Count > 0)
        {
            int cursor = Math.Clamp(_cursor, 0, visible.Count - 1);
            int start = visible.Count <= bodyRows
                ? 0
                : Math.Clamp(cursor - (bodyRows / 2), 0, visible.Count - bodyRows);
            int bodyLimit = hasDetail ? height - 1 : height;
            for (int i = start; i < visible.Count && rows.Count < bodyLimit; i++)
            {
                string prefix = i == cursor ? "> " : "  ";
                rows.Add(new HistoryRow(
                    UnicodeTruncation.Truncate(prefix + visible[i].Text, width, UnicodeTruncation.Ellipsis),
                    i == cursor));
            }
        }

        if (hasDetail)
        {
            HistoryEntry? selected = Selected;
            rows.Add(new HistoryRow(
                UnicodeTruncation.Truncate(selected?.Detail ?? string.Empty, width, UnicodeTruncation.Ellipsis),
                false));
        }

        return rows;
    }

    /// <summary>Paints the projected rows inside <paramref name="clip"/>.</summary>
    public void Paint(ScreenBuffer buffer, Rect clip, CellStyle style = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (clip.Width <= 1 || clip.Height <= 0)
        {
            return;
        }

        IReadOnlyList<HistoryRow> rows = Rows(clip.Width, clip.Height);
        var cursorStyle = new CellStyle(attrs: StyleAttr.Reverse);
        for (int i = 0; i < rows.Count; i++)
        {
            HistoryRow row = rows[i];
            buffer.SetText(clip.X, clip.Y + i, row.Text.AsSpan(), row.IsCursor ? cursorStyle : style);
        }
    }

    private bool ClampCursor()
    {
        int limit = Math.Max(0, Visible().Count - 1);
        int next = Math.Clamp(_cursor, 0, limit);
        if (next == _cursor)
        {
            return false;
        }

        _cursor = next;
        return true;
    }

    private string FacetLine()
    {
        if (_facets.Count == 0)
        {
            return "facets: all";
        }

        var line = new StringBuilder("facets:");
        for (int i = 0; i < _facets.Count; i++)
        {
            line.Append(' ').Append(_facets[i].Key).Append('=').Append(_facets[i].Value);
        }

        return line.ToString();
    }

    private static bool Matches(HistoryEntry entry, IReadOnlyList<FacetSelection> facets, string query)
    {
        if (query.Length != 0 &&
            !entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !entry.Detail.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = 0; i < facets.Count; i++)
        {
            string key = facets[i].Key;

            bool seen = false;
            for (int k = 0; k < i; k++)
            {
                if (string.Equals(facets[k].Key, key, StringComparison.Ordinal))
                {
                    seen = true;
                    break;
                }
            }

            if (seen)
            {
                continue;
            }

            bool keyMatches = false;
            for (int j = 0; j < entry.Facets.Count; j++)
            {
                HistoryFacet facet = entry.Facets[j];
                if (!string.Equals(facet.Key, key, StringComparison.Ordinal))
                {
                    continue;
                }

                for (int m = i; m < facets.Count; m++)
                {
                    if (string.Equals(facets[m].Key, key, StringComparison.Ordinal) &&
                        string.Equals(facets[m].Value, facet.Value, StringComparison.Ordinal))
                    {
                        keyMatches = true;
                        break;
                    }
                }

                if (keyMatches)
                {
                    break;
                }
            }

            if (!keyMatches)
            {
                return false;
            }
        }

        return true;
    }
}
