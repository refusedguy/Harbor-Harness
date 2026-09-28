namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// One floating layer above the panel layout (PRIM2a overlay plane): a dialog,
/// toast, palette, or any future floater. Layers stack bottom-to-top in
/// <see cref="OverlayStack"/> push order and paint after the
/// <see cref="LayoutTree"/> panels (btea Compositor pattern: base layout first,
/// floaters on top, input routed top-down).
/// </summary>
public interface IOverlayLayer
{
    /// <summary>Stable layer id; pushing a layer with an existing id moves it to the top.</summary>
    string Id { get; }

    /// <summary>Screen-space bounds the layer may paint within. Clipped to the viewport on paint.</summary>
    Rect Bounds { get; }

    /// <summary>Hidden layers are skipped by both paint and hit-test.</summary>
    bool Visible { get; }

    /// <summary>
    /// Opaque layers occlude layers beneath: the occluded fragments are clipped away
    /// before lower layers paint (overlap clipping), so lower layers never overpaint
    /// cells the opaque layer will cover. Non-opaque layers (shadows, dimming veils)
    /// still let lower fragments paint underneath them.
    /// </summary>
    bool Opaque { get; }

    /// <summary>
    /// Hit-transparent layers never win hit-tests — pointer events fall through to
    /// layers beneath (e.g. toasts). Non-transparent layers capture the event.
    /// </summary>
    bool HitTransparent { get; }

    /// <summary>
    /// Modal layers form a TGui-style input barrier: while any visible modal layer
    /// sits on the stack, keys must not reach panels beneath it (ENG4 #275).
    /// Defaults to false so PRIM2a layers (toasts, veils) stay non-blocking.
    /// </summary>
    bool IsModal => false;

    /// <summary>
    /// Offers a key to the layer, top-down (btea message-routing pattern).
    /// Returns true when consumed. Default is false (observe-only layer).
    /// </summary>
    bool OnKey(in KeyEvent key) => false;

    /// <summary>
    /// Paints one visible <paramref name="clip"/> fragment. The clip is already
    /// intersected with the viewport and occlusion-subtracted against opaque layers
    /// above; the layer must not paint outside it.
    /// </summary>
    void Paint(ScreenBuffer buffer, Rect clip);
}

/// <summary>
/// Z-ordered overlay stack over <see cref="LayoutTree.PaintAll"/> (PRIM2a, btea
/// Compositor pattern). Paint runs bottom-to-top with overlap clipping; hit-test
/// and key routing run top-down. ENG4 (#275) adds the TGui modal barrier
/// (<see cref="IOverlayLayer.IsModal"/>, <see cref="HasModalBarrier"/>,
/// <see cref="RouteKey"/>) and the Ratatui Clear-popup helpers
/// (<see cref="OverlayPopup"/>). Empty-stack paint is a no-op — steady-state
/// frames and goldens stay byte-identical until PRIM2c seats
/// DialogOverlay/ToastOverlay on the stack.
/// Not thread-safe: push/remove/paint/route on the render thread only.
/// </summary>
public sealed class OverlayStack
{
    private readonly List<IOverlayLayer> _layers = [];

    /// <summary>Layers bottom-to-top (paint order).</summary>
    public IReadOnlyList<IOverlayLayer> Layers => _layers;

    public int Count => _layers.Count;

    public bool IsEmpty => _layers.Count == 0;

    /// <summary>
    /// Pushes a layer on top. A layer with the same <see cref="IOverlayLayer.Id"/>
    /// is replaced and moved to the top.
    /// </summary>
    public void Push(IOverlayLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentException.ThrowIfNullOrWhiteSpace(layer.Id);
        for (int i = 0; i < _layers.Count; i++)
        {
            if (string.Equals(_layers[i].Id, layer.Id, StringComparison.Ordinal))
            {
                _layers.RemoveAt(i);
                break;
            }
        }

        _layers.Add(layer);
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

    public IOverlayLayer? Get(string id)
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

    /// <summary>
    /// Topmost visible modal layer (TGui modal-stack pattern), or null when no
    /// barrier is active. Hidden modals never block.
    /// </summary>
    public IOverlayLayer? TopModal
    {
        get
        {
            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                var layer = _layers[i];
                if (layer.Visible && layer.IsModal)
                {
                    return layer;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// True while a visible modal layer sits on the stack: panels beneath must
    /// starve (TGui barrier). Host key-routing pattern:
    /// <code>
    /// if (overlays.RouteKey(key)) return true;      // overlay consumed
    /// if (overlays.HasModalBarrier) return true;    // swallowed by the barrier
    /// // ... route to the focused panel
    /// </code>
    /// </summary>
    public bool HasModalBarrier => TopModal is not null;

    /// <summary>
    /// Routes a key top-down through visible layers (btea pattern): the first
    /// layer whose <see cref="IOverlayLayer.OnKey"/> returns true consumes it.
    /// Routing stops at the topmost visible modal — layers (and panels) beneath
    /// a modal never see the key. Returns true when consumed, false otherwise
    /// (when false with <see cref="HasModalBarrier"/> set, the host must still
    /// swallow the key so panels starve).
    /// </summary>
    public bool RouteKey(in KeyEvent key)
    {
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible)
            {
                continue;
            }

            if (layer.OnKey(in key))
            {
                return true;
            }

            if (layer.IsModal)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Paints every visible layer bottom-to-top. Each layer receives its bounds
    /// clipped to the viewport minus the opaque layers above it (overlap
    /// clipping); fully occluded layers are skipped. No-op when empty.
    /// </summary>
    public void PaintOver(ScreenBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_layers.Count == 0)
        {
            return;
        }

        var screen = new Rect(0, 0, buffer.Cols, buffer.Rows);
        // Lists are per-call locals (overlays are off the steady-state hot path:
        // empty-stack paint returns before any allocation).
        var frags = new List<Rect>(4);
        var occluders = new List<Rect>(4);
        for (int i = 0; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            if (!layer.Visible)
            {
                continue;
            }

            var bounds = layer.Bounds.Intersect(screen);
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

                var occluder = above.Bounds.Intersect(screen);
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

            for (int f = 0; f < frags.Count; f++)
            {
                layer.Paint(buffer, frags[f]);
            }
        }
    }

    /// <summary>
    /// Top-down hit-test: the topmost visible, non-<see cref="IOverlayLayer.HitTransparent"/>
    /// layer containing (<paramref name="col"/>, <paramref name="row"/>) wins, or null.
    /// </summary>
    public IOverlayLayer? HitTest(int col, int row)
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
            // Left strip (full height).
            if (cut.X > frag.X)
            {
                frags.Add(new Rect(frag.X, frag.Y, cut.X - frag.X, frag.Height));
            }

            // Right strip (full height).
            if (cut.Right < frag.Right)
            {
                frags.Add(new Rect(cut.Right, frag.Y, frag.Right - cut.Right, frag.Height));
            }

            // Top strip (between the cut's horizontal edges).
            if (cut.Y > frag.Y)
            {
                frags.Add(new Rect(cut.X, frag.Y, cut.Width, cut.Y - frag.Y));
            }

            // Bottom strip (between the cut's horizontal edges).
            if (cut.Bottom < frag.Bottom)
            {
                frags.Add(new Rect(cut.X, cut.Bottom, cut.Width, frag.Bottom - cut.Bottom));
            }
        }
    }
}

/// <summary>
/// Ratatui Clear-popup pattern for overlay floaters (ENG4 #275): a popup first
/// blanks its footprint with <see cref="Clear"/> (the Ratatui <c>Clear</c>
/// widget — no ghost text from panels beneath), then paints its widget inside
/// <see cref="CenteredRect"/>-computed bounds. PRIM2c dialog/toast layers use
/// these helpers when they seat on the <see cref="OverlayStack"/>.
/// </summary>
public static class OverlayPopup
{
    /// <summary>
    /// Centers a <paramref name="width"/>×<paramref name="height"/> popup inside
    /// <paramref name="screen"/> (Ratatui <c>centered_rect</c> pattern).
    /// Oversized requests clamp to the screen; non-positive sizes yield an
    /// empty rect.
    /// </summary>
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

    /// <summary>
    /// Blanks <paramref name="rect"/> to empty cells (Ratatui <c>Clear</c>).
    /// Clipped to the viewport by <see cref="ScreenBuffer.Fill"/>.
    /// </summary>
    public static void Clear(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        buffer.Fill(rect, Cell.Blank);
    }
}
