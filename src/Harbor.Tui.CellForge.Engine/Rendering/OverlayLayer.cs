// TR3 (#1222, epic #1195): generalized overlay/z-layer primitive —
// ZLayer{id,z,modal} over ScreenBuffer for ANY screen (toasts, dialogs,
// palettes), not just chat. Behavior port of Chat/Rendering/OverlayStack.cs
// (push/replace, overlap clipping, top-down hit-test, modal barrier) through
// the INode contract; Termina ModalNode lends the barrier, ConsoleEx portals
// lend the seat-at-bounds painting. Leaf rule: BCL-only, AOT-compatible,
// zero Harbor references, render-thread owned, chat untouched.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// One floating entry above any screen (toast, dialog, palette, dropdown).
/// Portal semantics: <see cref="Content"/> is arranged at <see cref="Bounds"/>
/// and paints only there, clipped to the viewport.
/// </summary>
public sealed class ZLayer
{
    private Rect _bounds;

    public ZLayer(string id, INode content, Rect bounds, int z = 0, bool modal = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(content);
        Id = id;
        Content = content;
        _bounds = Normalize(bounds);
        Z = z;
        Modal = modal;
    }

    /// <summary>Stable id; re-push replaces and re-seats the entry.</summary>
    public string Id { get; }

    /// <summary>Painted content (any <see cref="INode"/>).</summary>
    public INode Content { get; }

    /// <summary>Screen-space footprint (negative extents count as zero).</summary>
    public Rect Bounds
    {
        get => _bounds;
        set => _bounds = Normalize(value);
    }

    /// <summary>Z-order: lower paints first, ties keep push order.</summary>
    public int Z { get; set; }

    /// <summary>Modal entries barrier entries beneath (see <see cref="OverlayLayer.HasModalBarrier"/>).</summary>
    public bool Modal { get; set; }

    /// <summary>Hidden entries skip paint, hit-test and routing.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Opaque entries clip the entries beneath; veils stay non-opaque.</summary>
    public bool Opaque { get; set; } = true;

    /// <summary>Hit-transparent entries never win hit-tests (e.g. toasts).</summary>
    public bool HitTransparent { get; set; }

    private static Rect Normalize(Rect rect) =>
        new(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));
}

/// <summary>
/// Z-ordered overlay compositor over any <see cref="ScreenBuffer"/> (chat-agnostic
/// twin of the chat <c>OverlayStack</c>): paint bottom-to-top with overlap clipping,
/// hit-test and offer routing top-down. Empty paint is a no-op. Not thread-safe.
/// </summary>
public sealed class OverlayLayer : ContainerNode
{
    private readonly List<ZLayer> _layers = [];

    /// <summary>Entries bottom-to-top (paint order).</summary>
    public IReadOnlyList<ZLayer> Layers => _layers;

    public int Count => _layers.Count;
    public bool IsEmpty => _layers.Count == 0;

    /// <summary>Seats the entry at its (Z, push-order) position; same id replaces.</summary>
    public void Push(ZLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        for (int i = 0; i < _layers.Count; i++)
        {
            if (string.Equals(_layers[i].Id, layer.Id, StringComparison.Ordinal))
            {
                _layers.RemoveAt(i);
                break;
            }
        }

        int at = _layers.Count;
        for (int i = 0; i < _layers.Count; i++)
        {
            if (layer.Z < _layers[i].Z)
            {
                at = i;
                break;
            }
        }

        _layers.Insert(at, layer);
    }

    public bool Remove(string id)
    {
        for (int i = 0; i < _layers.Count; i++)
        {
            if (string.Equals(_layers[i].Id, id, StringComparison.Ordinal))
            {
                _layers.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    public void Clear() => _layers.Clear();

    public ZLayer? Get(string id)
    {
        for (int i = 0; i < _layers.Count; i++)
        {
            if (string.Equals(_layers[i].Id, id, StringComparison.Ordinal))
            {
                return _layers[i];
            }
        }

        return null;
    }

    /// <summary>Topmost visible modal entry, or null while no barrier is active.</summary>
    public ZLayer? TopModal
    {
        get
        {
            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                var layer = _layers[i];
                if (layer.Visible && layer.Modal)
                {
                    return layer;
                }
            }

            return null;
        }
    }

    /// <summary>True while a visible modal sits on the stack: entries beneath starve.</summary>
    public bool HasModalBarrier => TopModal is not null;

    /// <summary>
    /// Offers entries top-down through <paramref name="offer"/> (no input-vocabulary
    /// dependency — the host closes over its own key type). Stops at the topmost
    /// visible modal. True when consumed; false with <see cref="HasModalBarrier"/>
    /// set still means swallow, so beneath starves.
    /// </summary>
    public bool RouteKey(Func<ZLayer, bool> offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible)
            {
                continue;
            }

            if (offer(layer))
            {
                return true;
            }

            if (layer.Modal)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Topmost visible, non-transparent entry containing (col, row), or null.</summary>
    public ZLayer? HitTest(int col, int row)
    {
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible || layer.HitTransparent)
            {
                continue;
            }

            if (layer.Bounds.Contains(col, row))
            {
                return layer;
            }
        }

        return null;
    }

    /// <summary>Desired size: clamped max of the visible contents (pure, like <see cref="ZStackNode"/>).</summary>
    public override Size Measure(Constraints constraints)
    {
        int width = 0;
        int height = 0;
        for (int i = 0; i < _layers.Count; i++)
        {
            if (!_layers[i].Visible)
            {
                continue;
            }

            Size want = _layers[i].Content.Measure(constraints);
            if (want.Width > width)
            {
                width = want.Width;
            }

            if (want.Height > height)
            {
                height = want.Height;
            }
        }

        return new Size(Math.Min(Math.Max(0, width), constraints.Width), Math.Min(Math.Max(0, height), constraints.Height));
    }

    /// <summary>Seats every content at its entry bounds (portal semantics). No painting.</summary>
    public override void Arrange(Rect rect)
    {
        SetArranged(rect);
        for (int i = 0; i < _layers.Count; i++)
        {
            _layers[i].Content.Arrange(_layers[i].Bounds);
        }
    }

    /// <summary>Paints visible entries bottom-to-top, occluded fragments clipped away.</summary>
    public override void Paint(ScreenBuffer buffer, Rect clip)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_layers.Count == 0)
        {
            return;
        }

        var screen = new Rect(0, 0, buffer.Cols, buffer.Rows);
        if (Arranged.Width > 0 && Arranged.Height > 0)
        {
            screen = screen.Intersect(Arranged);
        }

        var viewport = screen.Intersect(clip);
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        // Per-call locals (off the steady-state hot path: empty paint returns first).
        var frags = new List<Rect>(4);
        var occluders = new List<Rect>(4);
        for (int i = 0; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            if (!layer.Visible)
            {
                continue;
            }

            var bounds = layer.Bounds.Intersect(viewport);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            occluders.Clear();
            for (int j = i + 1; j < _layers.Count; j++)
            {
                var above = _layers[j];
                if (!above.Visible || !above.Opaque)
                {
                    continue;
                }

                var occluder = above.Bounds.Intersect(viewport);
                if (occluder.Width > 0 && occluder.Height > 0)
                {
                    occluders.Add(occluder);
                }
            }

            frags.Clear();
            frags.Add(bounds);
            for (int k = 0; k < occluders.Count && frags.Count > 0; k++)
            {
                SubtractAll(frags, occluders[k]);
            }

            if (frags.Count == 0)
            {
                continue;
            }

            for (int f = 0; f < frags.Count; f++)
            {
                layer.Content.Paint(buffer, frags[f]);
            }
        }
    }

    private static void SubtractAll(List<Rect> frags, Rect occluder)
    {
        for (int i = frags.Count - 1; i >= 0; i--)
        {
            var frag = frags[i];
            var cut = frag.Intersect(occluder);
            if (cut.Width <= 0 || cut.Height <= 0)
            {
                continue;
            }

            frags.RemoveAt(i);
            if (cut.X > frag.X)
            {
                frags.Add(new Rect(frag.X, frag.Y, cut.X - frag.X, frag.Height));
            }

            if (cut.Right < frag.Right)
            {
                frags.Add(new Rect(cut.Right, frag.Y, frag.Right - cut.Right, frag.Height));
            }

            if (cut.Y > frag.Y)
            {
                frags.Add(new Rect(cut.X, frag.Y, cut.Width, cut.Y - frag.Y));
            }

            if (cut.Bottom < frag.Bottom)
            {
                frags.Add(new Rect(cut.X, cut.Bottom, cut.Width, frag.Bottom - cut.Bottom));
            }
        }
    }
}

/// <summary>
/// Ratatui Clear-popup pattern for floaters (chat <c>OverlayPopup</c> twin under a
/// collision-free name): blank the footprint with <see cref="Clear"/>, then paint
/// inside <see cref="CenteredRect"/>-computed bounds.
/// </summary>
public static class OverlayGeometry
{
    /// <summary>Centers width×height inside screen (oversized clamps, non-positive empties).</summary>
    public static Rect CenteredRect(Rect screen, int width, int height)
    {
        int w = Math.Min(width, screen.Width);
        int h = Math.Min(height, screen.Height);
        if (w <= 0 || h <= 0)
        {
            return default;
        }

        return new Rect(
            screen.X + ((screen.Width - w) / 2),
            screen.Y + ((screen.Height - h) / 2),
            w,
            h);
    }

    /// <summary>Blanks rect to empty cells (Ratatui <c>Clear</c>).</summary>
    public static void Clear(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        buffer.Fill(rect, Cell.Blank);
    }
}
