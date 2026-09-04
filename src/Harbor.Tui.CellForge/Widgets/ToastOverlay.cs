using Harbor.Ui.Framework.Rendering;

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
        int bottom = Math.Min(rect.Y + _active.Count - 1, rect.Bottom - 1);
        int painted = 0;
        for (int i = 0; i < _active.Count && rect.Y + painted <= bottom; i++)
        {
            PaintToast(buffer, rect.X, rect.Y + painted, right, _active[i]);
            painted++;
            if (painted >= MaxVisible)
            {
                break;
            }
        }
    }

    private void PaintToast(ScreenBuffer buffer, int left, int top, int right, ToastNotification toast)
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
        string prefix = Glyph(toast.Kind) + " ";
        string combined = prefix + toast.Message;
        if (combined.Length > textW)
        {
            combined = combined[..Math.Max(0, textW - 1)] + "…";
        }
        buffer.SetText(textX, top, combined, textStyle);
    }

    private void DrainPending()
    {
        while (_pending.Count > 0 && _active.Count < MaxVisible)
        {
            _active.Add(_pending.Dequeue());
        }
    }

    private static string Glyph(ToastKind kind) => kind switch
    {
        ToastKind.Success => "✓",
        ToastKind.Warning => "!",
        ToastKind.Error => "✗",
        _ => "ℹ",
    };
}