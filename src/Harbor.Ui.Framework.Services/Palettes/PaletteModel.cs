namespace Harbor.Ui.Framework.Overlays;

// TODO(principles)[DIP, OCP]: framework-owned palette navigation state machine.
// CellForge's CommandPaletteView must delegate frame/query/selection transitions
// here (adapter + Paint stay cell-side); all key semantics resolve via UiMsg so
// every renderer shares one experience. Wiring lands with epic C (TEA input loop).

/// <summary>
///     Generic drill-down palette model: frame stack, query, selection and
///     async continuations. Pure BCL, zero Harbor dependencies, zero rendering —
///     any renderer (cell, Spectre, GUI) drives it and paints from
///     <see cref="VisibleItems" />. Key semantics live here, not in renderers.
/// </summary>
/// <typeparam name="TItem">Palette entry payload (hosts map it to display text).</typeparam>
public sealed class PaletteModel<TItem>
{
    private readonly Stack<PaletteFrame<TItem>> _frames = new();
    private readonly Func<string, TItem, string> _textOf;
    private List<TItem> _results = [];
    private string _query = string.Empty;
    private int _selected;

    private (TItem Item, Func<TItem, CancellationToken, Task> Handler)? _pendingCommit;
    private (string Value, Func<string, CancellationToken, Task> Handler)? _pendingInput;

    /// <summary>Create a palette model over item display text.</summary>
    /// <param name="textOf">Display text selector used for filtering/sorting.</param>
    public PaletteModel(Func<string, TItem, string> textOf)
    {
        _textOf = textOf ?? throw new ArgumentNullException(nameof(textOf));
    }

    /// <summary>Whether any frame is open.</summary>
    public bool Visible { get; private set; }

    /// <summary>Current filter text.</summary>
    public string Query => _query;

    /// <summary>Current breadcrumb (empty for root).</summary>
    public string CurrentBreadcrumb => _frames.Count > 0 ? _frames.Peek().Breadcrumb : string.Empty;

    /// <summary>Filtered/ranked result set (all items when the query is empty).</summary>
    public IReadOnlyList<TItem> VisibleItems => _results;

    /// <summary>Index into <see cref="VisibleItems" />.</summary>
    public int SelectedIndex => _selected;

    /// <summary>Replace the whole stack with a single root frame.</summary>
    public void Show(PaletteFrame<TItem> root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _frames.Clear();
        PushFrame(root);
    }

    /// <summary>Close everything (drops pending continuations with the frames).</summary>
    public void Hide()
    {
        _frames.Clear();
        _pendingCommit = null;
        _pendingInput = null;
        Visible = false;
        _results = [];
        _query = string.Empty;
        _selected = 0;
    }

    /// <summary>Push a drill-down frame (clears stale pending continuations).</summary>
    public void PushFrame(PaletteFrame<TItem> frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frames.Push(frame);
        _pendingCommit = null;
        _pendingInput = null;
        _query = string.Empty;
        _selected = 0;
        Visible = true;
        Refilter();
    }

    /// <summary>Pop one level; false when the stack empties (caller hides).</summary>
    public bool PopFrame()
    {
        if (_frames.Count > 1)
        {
            _frames.Pop();
            _pendingCommit = null;
            _pendingInput = null;
            var prev = _frames.Peek();
            _query = string.Empty;
            _selected = 0;
            Refilter();
            _ = prev;
            return true;
        }

        Hide();
        return false;
    }

    /// <summary>Take the staged commit continuation (cleared on read).</summary>
    public (TItem Item, Func<TItem, CancellationToken, Task> Handler)? TakePendingCommit()
    {
        var p = _pendingCommit;
        _pendingCommit = null;
        return p;
    }

    /// <summary>Take the staged input continuation (cleared on read).</summary>
    public (string Value, Func<string, CancellationToken, Task> Handler)? TakePendingInput()
    {
        var p = _pendingInput;
        _pendingInput = null;
        return p;
    }

    /// <summary>Type a character into the filter.</summary>
    public void Type(char c)
    {
        _query += c;
        Refilter();
    }

    /// <summary>Backspace: trim the query, or pop when the query is empty.</summary>
    public void Backspace() => _ = _query.Length > 0 ? TrimQuery() : PopFrame();

    /// <summary>Move selection by delta (clamped).</summary>
    public void Move(int delta)
    {
        if (_results.Count == 0)
        {
            return;
        }

        _selected = Math.Clamp(_selected + delta, 0, _results.Count - 1);
    }

    /// <summary>Escape: pop one level (or hide at root).</summary>
    public void Escape() => _ = PopFrame();

    /// <summary>Enter: stage the commit/input continuation for the frame loop.</summary>
    public void Submit()
    {
        if (_frames.Count == 0)
        {
            return;
        }

        var top = _frames.Peek();
        if (top.IsInput)
        {
            var val = _query.Trim();
            if (top.OnInputSubmitAsync is { } submitAsync)
            {
                _pendingInput = (val, submitAsync);
            }

            return;
        }

        if (_results.Count > 0 && top.OnCommitAsync is { } commitAsync)
        {
            int index = Math.Min(_selected, _results.Count - 1);
            _pendingCommit = (_results[index], commitAsync);
        }
    }

    private bool TrimQuery()
    {
        _query = _query[..^1];
        Refilter();
        return true;
    }

    private void Refilter()
    {
        if (_frames.Count == 0)
        {
            _results = [];
            _selected = 0;
            return;
        }

        var items = _frames.Peek().Items;
        if (_query.Length == 0)
        {
            _results = items.OrderBy(i => _textOf(_query, i), StringComparer.Ordinal).ToList();
        }
        else
        {
            var scored = new List<(TItem Item, int Score)>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                int score = FuzzyScore(_textOf(_query, items[i]), _query);
                if (score >= 0)
                {
                    scored.Add((items[i], score));
                }
            }

            scored.Sort(static (a, b) => a.Score.CompareTo(b.Score));
            _results = scored.Select(p => p.Item).ToList();
        }

        _selected = 0;
    }

    // Subsequence fuzzy match; lower score = better (earlier, tighter match).
    private static int FuzzyScore(string text, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }

        int ti = 0;
        int score = 0;
        int lastHit = -1;
        for (int qi = 0; qi < query.Length; qi++)
        {
            char q = char.ToLowerInvariant(query[qi]);
            bool hit = false;
            while (ti < text.Length)
            {
                if (char.ToLowerInvariant(text[ti]) == q)
                {
                    score += ti - lastHit - 1;
                    lastHit = ti;
                    ti++;
                    hit = true;
                    break;
                }

                ti++;
            }

            if (!hit)
            {
                return -1;
            }
        }

        return score;
    }
}

/// <summary>Generic drill-down frame: items plus its own async continuations.</summary>
public sealed record PaletteFrame<TItem>(
    string Title,
    string Breadcrumb,
    IReadOnlyList<TItem> Items,
    Func<TItem, CancellationToken, Task>? OnCommitAsync = null,
    bool IsInput = false,
    string InputPlaceholder = "",
    Func<string, CancellationToken, Task>? OnInputSubmitAsync = null);
