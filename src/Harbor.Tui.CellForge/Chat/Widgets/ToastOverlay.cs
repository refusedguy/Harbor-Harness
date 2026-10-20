using System.Text;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Services;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Cell-native toast overlay (CellForge EPIC H).
/// Renders queued toast notifications above the chat feed, stacked
/// top-to-bottom, with a 3-cell accent strip on the left edge and a
/// 280-380 px content area (capped to the terminal width).
/// Pure over its queue state — no layout side effects.
/// </summary>
public sealed class ToastOverlay
{
    private const int MinWidth = 28;
    private const int MaxWidth = 38;
    private const int AccentWidth = 3;
    private const int MinInnerHeight = 1;
    private const int MaxVisible = 4;
    private const int DefaultDismissAfterTicks = 240;

    /// <summary>
    ///     Cells a toast row may take: the glyph, the separator, the message
    ///     and the ellipsis. The row is bounded by the toast width, which is
    ///     bounded by <see cref="MaxWidth" />.
    /// </summary>
    private const int MaxRowChars = MaxWidth + 2;

    private readonly List<ToastNotification> _active = new();
    private readonly Queue<ToastNotification> _pending = new();
    private long _tick;

    public ToastOverlay()
    {
    }

    /// <summary>Number of toasts currently painted (visible + queued).</summary>
    public int Count => _active.Count + _pending.Count;

    public bool IsEmpty => _active.Count == 0 && _pending.Count == 0;

    public void Show(string message, ToastKind kind = ToastKind.Info)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }
        _pending.Enqueue(new ToastNotification(message, kind));
    }

    public bool Dismiss(Guid id)
    {
        for (int i = 0; i < _active.Count; i++)
        {
            if (_active[i].Id != id)
            {
                continue;
            }
            _active.RemoveAt(i);
            DrainPending();
            return true;
        }
        return false;
    }

    public void Clear()
    {
        _active.Clear();
        _pending.Clear();
    }

    public void Tick()
    {
        _tick++;
        if (_active.Count > 0 && _tick % DefaultDismissAfterTicks == 0)
        {
            _active.RemoveAt(0);
            DrainPending();
        }
    }

    public IReadOnlyList<ToastNotification> Active => _active;

    /// <summary>
    /// Computes the toast strip for <paramref name="viewport"/>: top-right with a
    /// 1-cell margin, one row per visible toast (capped to <c>MaxVisible</c>).
    /// Returns default when empty or the viewport is too small. Single source of
    /// truth for the PRIM2c overlay-layer seating (<see cref="ToastOverlayLayer"/>
    /// reports this as its bounds; <see cref="Paint"/> keeps painting at the
    /// given rect origin so manual-blit callers are unaffected).
    /// </summary>
    public Rect ComputeBounds(Rect viewport)
    {
        if (IsEmpty)
        {
            return default;
        }
        if (viewport.Width < MinWidth + AccentWidth || viewport.Height < MinInnerHeight)
        {
            return default;
        }

        int count = Math.Min(Count, MaxVisible);
        if (count <= 0)
        {
            return default;
        }

        int width = Math.Min(MaxWidth, Math.Min(MinWidth, viewport.Width - AccentWidth));
        int totalW = width + AccentWidth;
        int x = viewport.Right - totalW - 1;
        if (x < viewport.X)
        {
            x = viewport.X;
        }
        int y = viewport.Y + 1;
        return new Rect(x, y, totalW, count);
    }

    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        if (_active.Count == 0)
        {
            DrainPending();
            return;
        }
        if (rect.Width < MinWidth + AccentWidth || rect.Height < MinInnerHeight)
        {
            return;
        }
        if (rect.X >= buffer.Cols || rect.Y >= buffer.Rows)
        {
            return;
        }

        int width = Math.Min(MaxWidth, Math.Min(MinWidth, rect.Width - AccentWidth));
        int right = rect.X + width + AccentWidth - 1;
        // No snapshot: _active is owned by the render thread (mutated only via
        // Show/Dismiss/Tick/Clear on the owning ChatScreenLayout; Paint runs on
        // the same pass) and never published, so a per-paint ToArray copied a
        // list nothing else can touch mid-pass. Capped index loop: at most
        // MaxVisible reads, no allocation.
        int count = Math.Min(_active.Count, MaxVisible);
        int bottom = Math.Min(rect.Y + count - 1, rect.Bottom - 1);
        int painted = 0;
        // One row buffer for the whole strip. The row used to be built as a
        // fresh string per toast per frame ("<glyph> " + message, plus a
        // truncated copy when it did not fit) — two allocations per toast on
        // every frame the toast was up. The buffer is allocated once per
        // painted strip, on the stack, and never escapes.
        Span<char> row = stackalloc char[MaxRowChars];
        for (int i = 0; i < count && rect.Y + painted <= bottom; i++)
        {
            PaintToast(buffer, rect.X, rect.Y + painted, right, _active[i], row);
            painted++;
        }
    }

    private void PaintToast(
        ScreenBuffer buffer,
        int left,
        int top,
        int right,
        ToastNotification toast,
        Span<char> row)
    {
        if (top >= buffer.Rows || right >= buffer.Cols)
        {
            return;
        }

        var accentColor = toast.Kind switch
        {
            ToastKind.Success => ChatPalette.Success,
            ToastKind.Warning => ChatPalette.Warning,
            ToastKind.Error => ChatPalette.Error,
            _ => ChatPalette.Accent,
        };
        var accentStyle = new CellStyle(accentColor);
        var fillStyle = new CellStyle(ChatPalette.Panel);
        var textStyle = new CellStyle(ChatPalette.Accent);

        for (int x = left; x <= right; x++)
        {
            buffer.At(x, top) = Cell.From(new Rune(' '), fillStyle);
        }
        for (int dy = 0; dy < AccentWidth && top + dy < buffer.Rows; dy++)
        {
            buffer.At(left, top + dy) = Cell.From(new Rune('█'), accentStyle);
        }
        int textX = left + AccentWidth;
        int textW = right - textX + 1;

        // The row is "<glyph> <message>", truncated to textW - 1 characters
        // plus an ellipsis when it does not fit — the same characters, in the
        // same order, as the concatenated string the row used to be.
        int full = 2 + toast.Message.Length;
        bool ellipsis = full > textW;
        int take = ellipsis ? Math.Max(0, textW - 1) : full;

        int len = 0;
        if (take > 0)
        {
            row[len++] = Glyph(toast.Kind);
        }
        if (take > 1)
        {
            row[len++] = ' ';
        }

        int message = Math.Min(Math.Max(0, take - 2), toast.Message.Length);
        if (message > 0)
        {
            toast.Message.AsSpan(0, message).CopyTo(row[len..]);
            len += message;
        }

        if (ellipsis)
        {
            row[len++] = '…';
        }

        buffer.SetText(textX, top, row[..len], textStyle);
    }

    private void DrainPending()
    {
        while (_pending.Count > 0 && _active.Count < MaxVisible)
        {
            _active.Add(_pending.Dequeue());
        }
    }

    /// <summary>
    ///     The row's leading glyph. A char, not a string: the row is written
    ///     into a caller-owned buffer, and a one-character string per toast per
    ///     frame would be an allocation for a constant.
    /// </summary>
    private static char Glyph(ToastKind kind) => kind switch
    {
        ToastKind.Success => '✓',
        ToastKind.Warning => '!',
        ToastKind.Error => '✗',
        _ => 'ℹ',
    };
}