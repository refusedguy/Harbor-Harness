using System.Collections.Generic;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Addressable damage ledger owned by <see cref="UiStore"/> (issue #2354).
///     Producers mark the view subgraphs a state change touched, either by
///     view id or by explicit <see cref="Rect"/>; the frame thread drains the
///     ledger once per frame and resolves the marks through the engine's
///     <c>DirtyRect</c> tree into narrow diff hints. A broad mark is the
///     conservative escape hatch (resize, theme swap, untrackable layout).
/// </summary>
/// <remarks>
///     Unlike <see cref="UiStore.Dispatch"/>, marks arrive from several
///     threads (input loop, agent pipeline, frame loop), so the pending set
///     is guarded by a small lock. The lock is off the dispatch hot path:
///     only mark/drain take it, never a state fold.
/// </remarks>
public sealed class UiDamageLedger
{
    private readonly object _gate = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly List<Rect> _rects = [];
    private bool _broad;

    /// <summary>Pending id + rect marks not yet drained.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _ids.Count + _rects.Count + (_broad ? 1 : 0);
            }
        }
    }

    /// <summary>Marks one view subgraph dirty.</summary>
    public void Damage(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        lock (_gate)
        {
            _ids.Add(id);
        }
    }

    /// <summary>Marks several view subgraphs dirty.</summary>
    public void Damage(params string[] ids)
    {
        if (ids is null)
        {
            return;
        }

        lock (_gate)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                if (!string.IsNullOrEmpty(ids[i]))
                {
                    _ids.Add(ids[i]);
                }
            }
        }
    }

    /// <summary>Marks an arbitrary screen rect dirty.</summary>
    public void Damage(in Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _rects.Add(rect);
        }
    }

    /// <summary>Marks the whole frame dirty (full-scan escape hatch).</summary>
    public void DamageAll()
    {
        lock (_gate)
        {
            _broad = true;
        }
    }

    /// <summary>
    ///     Replaces a broad (or rect) mark with one precise view id. Used when
    ///     a mutation turns out to touch a single subgraph (a plain composer
    ///     edit), so the conservative input-time mark can be narrowed.
    /// </summary>
    public void NarrowTo(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        lock (_gate)
        {
            _broad = false;
            _rects.Clear();
            _ids.Clear();
            _ids.Add(id);
        }
    }

    /// <summary>
    ///     Atomically takes the pending marks into caller-owned lists (reused
    ///     across frames, so the hot path allocates after warm-up). Returns
    ///     <see langword="true"/> for a broad mark; in that case the lists are
    ///     left empty. The ledger is always empty on return.
    /// </summary>
    public bool DrainInto(List<string> ids, List<Rect> rects)
    {
        lock (_gate)
        {
            bool broad = _broad;
            _broad = false;
            ids.Clear();
            rects.Clear();
            if (!broad)
            {
                ids.AddRange(_ids);
                rects.AddRange(_rects);
            }

            _ids.Clear();
            _rects.Clear();
            return broad;
        }
    }
}
