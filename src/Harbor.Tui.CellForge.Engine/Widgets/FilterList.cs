using System.Collections;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// App-agnostic incremental fuzzy filter list: a generalized version of the
/// query → ranked results → selection loop shared by the command palette
/// (<c>CommandPaletteView</c>) and the jump palette (<c>JumpPalettePanel</c>),
/// with match highlighting spans and a preview callback added. It knows only
/// items, their search text and two callbacks — no commands, files, sessions
/// or any other application domain.
/// </summary>
/// <remarks>
/// <para>
/// <b>One fuzzy source.</b> Scoring goes through the shared
/// <see cref="FuzzyMatcher.Score(string, string)" /> (LRU-cached LCS-style
/// subsequence scorer) and match positions through
/// <see cref="FuzzyMatcher.MatchIndices(string, string, Span{int})" />. No
/// second scorer is introduced here — this is the "вынеси общее, не дублируй"
/// slice over the two palettes, lifted into a reusable engine primitive.
/// </para>
/// <para>
/// <b>Zero-alloc filtering.</b> Search text is extracted once at construction
/// (or <see cref="SetItems" />), so a keystroke never calls the text selector.
/// The ranked buffer, the narrowing marks and the result view are allocated
/// once and reused; refiltering walks the buffer in place and sorts only the
/// live prefix. With a warm <see cref="FuzzyMatcher" /> cache a refilter
/// allocates nothing.
/// </para>
/// <para>
/// <b>Incremental narrowing.</b> When a new query extends the previous one,
/// a longer subsequence match can only come from a row that already matched,
/// so only those rows are rescored. A shorter or divergent query falls back
/// to a full scan (correctness over cleverness).
/// </para>
/// </remarks>
/// <typeparam name="T">Opaque item type; the primitive never inspects it.</typeparam>
public sealed class FilterList<T>
{
    private readonly struct Row
    {
        public Row(T item, string text)
        {
            Item = item;
            Text = text;
        }

        public readonly T Item;
        public readonly string Text;
    }

    private readonly struct Ranked
    {
        public Ranked(int sourceIndex, int score)
        {
            SourceIndex = sourceIndex;
            Score = score;
        }

        public readonly int SourceIndex;
        public readonly int Score;
    }

    private sealed class RankedComparer : IComparer<Ranked>
    {
        public static readonly RankedComparer Instance = new();

        public int Compare(Ranked x, Ranked y)
        {
            int byScore = y.Score.CompareTo(x.Score); // best first
            return byScore != 0 ? byScore : x.SourceIndex.CompareTo(y.SourceIndex); // stable by input order
        }
    }

    private sealed class ResultsView : IReadOnlyList<T>
    {
        private readonly FilterList<T> _owner;

        public ResultsView(FilterList<T> owner) => _owner = owner;

        public T this[int index] => _owner[index];

        public int Count => _owner._resultCount;

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < _owner._resultCount; i++)
            {
                yield return _owner[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private readonly Func<T, string> _textOf;
    private Row[] _rows;
    private Ranked[] _ranked;
    private bool[] _prevMatched;
    private readonly ResultsView _results;
    private int _resultCount;
    private string _query = string.Empty;
    private string _lastQuery = string.Empty;
    private bool _hasLast;
    private int _selected;
    private Func<T, string>? _previewOf;
    private string _preview = string.Empty;

    /// <summary>
    /// Builds a filter over <paramref name="items" />. Search text is extracted
    /// once per item via <paramref name="textOf" />; <paramref name="previewOf" />
    /// is invoked for the selected item to produce the preview text.
    /// </summary>
    public FilterList(IReadOnlyList<T> items, Func<T, string> textOf, Func<T, string>? previewOf = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(textOf);

        _textOf = textOf;
        _previewOf = previewOf;
        _rows = new Row[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            T item = items[i];
            _rows[i] = new Row(item, textOf(item) ?? string.Empty);
        }

        _ranked = new Ranked[_rows.Length];
        _prevMatched = new bool[_rows.Length];
        _results = new ResultsView(this);
        Refilter();
    }

    /// <summary>Current filter text (empty means "all rows, input order").</summary>
    public string Query => _query;

    /// <summary>Ranked matches for the current query (best first).</summary>
    public IReadOnlyList<T> Results => _results;

    /// <summary>Number of current matches.</summary>
    public int Count => _resultCount;

    /// <summary>Match at <paramref name="index" /> in ranked order.</summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_resultCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _rows[_ranked[index].SourceIndex].Item;
        }
    }

    /// <summary>Index of the selected match (0 when empty).</summary>
    public int SelectedIndex => _selected;

    /// <summary>Selected item, or <c>default</c> when there are no matches.</summary>
    public T SelectedItem => _resultCount == 0 ? default! : this[_selected];

    /// <summary>Preview text for the selected item (empty when none or no callback).</summary>
    public string Preview => _preview;

    /// <summary>Preview producer, re-invoked whenever the selection changes.</summary>
    public Func<T, string>? PreviewOf
    {
        get => _previewOf;
        set
        {
            _previewOf = value;
            UpdatePreview();
        }
    }

    /// <summary>Replaces the source items, keeping the current query.</summary>
    public void SetItems(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var rows = new Row[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            T item = items[i];
            rows[i] = new Row(item, _textOf(item) ?? string.Empty);
        }

        // The row count may change; replace the reusable buffers so indices stay valid.
        _rows = rows;
        _ranked = new Ranked[rows.Length];
        _prevMatched = new bool[rows.Length];
        _lastQuery = string.Empty;
        _hasLast = false;
        Refilter();
    }

    /// <summary>
    /// Sets the filter text and refilters. A no-op for an unchanged query.
    /// </summary>
    public void SetQuery(string query)
    {
        query ??= string.Empty;
        if (string.Equals(query, _query, StringComparison.Ordinal))
        {
            return;
        }

        _query = query;
        _selected = 0;
        Refilter();
    }

    /// <summary>Moves the selection by a signed delta (clamped, no wrap).</summary>
    public void Move(int delta) => MoveTo(_selected + delta);

    /// <summary>Selects the next match (clamped at the end).</summary>
    public void SelectNext() => Move(1);

    /// <summary>Selects the previous match (clamped at the start).</summary>
    public void SelectPrevious() => Move(-1);

    /// <summary>Selects a match by index (clamped).</summary>
    public void MoveTo(int index)
    {
        if (_resultCount == 0)
        {
            _selected = 0;
            return;
        }

        _selected = Math.Clamp(index, 0, _resultCount - 1);
        UpdatePreview();
    }

    /// <summary>Search/display text of the match at <paramref name="resultIndex" />.</summary>
    public string TextOfResult(int resultIndex) =>
        (uint)resultIndex < (uint)_resultCount
            ? _rows[_ranked[resultIndex].SourceIndex].Text
            : string.Empty;

    /// <summary>
    /// Writes the highlight positions of <paramref name="resultIndex" /> into
    /// <paramref name="buffer" /> (leftmost subsequence, case-insensitive).
    /// Returns the position count, or 0 when there is no highlight (empty
    /// query, no match, or a buffer too small for the query).
    /// </summary>
    public int WriteMatchIndices(int resultIndex, Span<int> buffer)
    {
        if (_query.Length == 0 || (uint)resultIndex >= (uint)_resultCount)
        {
            return 0;
        }

        int sourceIndex = _ranked[resultIndex].SourceIndex;
        return FuzzyMatcher.MatchIndices(_query, _rows[sourceIndex].Text, buffer);
    }

    private void Refilter()
    {
        bool narrow = _hasLast
            && _query.Length > _lastQuery.Length
            && _query.StartsWith(_lastQuery, StringComparison.Ordinal);
        if (!narrow)
        {
            Array.Clear(_prevMatched);
        }

        string q = _query;
        bool empty = q.Length == 0;
        int count = 0;
        for (int i = 0; i < _rows.Length; i++)
        {
            if (!empty && narrow && !_prevMatched[i])
            {
                continue;
            }

            int score;
            if (empty)
            {
                score = 0;
            }
            else
            {
                int? match = FuzzyMatcher.Score(q, _rows[i].Text);
                if (!match.HasValue)
                {
                    if (narrow)
                    {
                        _prevMatched[i] = false; // drop a stale mark from the previous set
                    }

                    continue;
                }

                score = match.Value;
            }

            _prevMatched[i] = true;
            _ranked[count++] = new Ranked(i, score - i); // earlier rows win ties
        }

        _resultCount = count;
        if (count > 1)
        {
            Array.Sort(_ranked, 0, count, RankedComparer.Instance);
        }

        _lastQuery = q;
        _hasLast = true;
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (_resultCount == 0)
        {
            _selected = 0;
        }
        else if (_selected >= _resultCount)
        {
            _selected = _resultCount - 1;
        }

        UpdatePreview();
    }

    private void UpdatePreview()
    {
        _preview = _resultCount == 0 || _previewOf is null
            ? string.Empty
            : _previewOf(this[_selected]) ?? string.Empty;
    }
}
