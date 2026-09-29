using Harbor.Ui.Framework.Panels;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Passive context for the which-key help overlay (PRIM11, #306): which panel
/// is focused and which overlay sits on top of the z-stack. Pure data — the
/// overlay renders it, never routes keys (keymap logic stays in
/// <c>ChatKeyMap</c> / <c>HelpKeymap</c>, untouched).
/// </summary>
public sealed record WhichKeyContext(string? FocusedPanelId, string? ActiveOverlayId);

/// <summary>
/// Context-aware which-key help overlay (PRIM11, crush ctrl+g / codex
/// ?-overlay pattern): a centered modal for the PRIM2a z-stack rendering the
/// shared <see cref="HelpKeymap"/> rows read-only plus a context section
/// naming the focused panel and the active overlay when the host supplies
/// them. Mirrors the <c>DialogOverlay</c> modal language (rounded box, panel
/// fill, accent title) without touching it.
/// Hidden paint is a no-op so steady-state frames and goldens stay
/// byte-identical until the host shows it.
/// Not thread-safe: show/hide/paint on the render thread only.
/// </summary>
public sealed class WhichKeyHelpOverlay
{
    public const int MinWidth = 20;
    public const int MaxWidth = 72;
    public const int MinHeight = 5;
    public const int MaxHeight = 24;
    private const int Padding = 1;
    private const int Chrome = 2 + (Padding * 2);

    private WhichKeyContext? _context;

    public bool Visible { get; private set; }

    public WhichKeyContext? Context => _context;

    public bool HasContext =>
        !string.IsNullOrEmpty(_context?.FocusedPanelId) ||
        !string.IsNullOrEmpty(_context?.ActiveOverlayId);

    public void Show(WhichKeyContext? context = null)
    {
        _context = context;
        Visible = true;
    }

    public void Hide()
    {
        Visible = false;
    }

    public void SetContext(WhichKeyContext? context) => _context = context;

    /// <summary>
    /// Overlay-local dismissal (Esc / '?'), mirroring
    /// <c>DialogOverlay.HandleKey</c>. Anything else passes through untouched —
    /// chord resolution stays with the host keymap.
    /// </summary>
    public bool HandleKey(ConsoleKeyInfo key)
    {
        if (!Visible)
        {
            return false;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            Hide();
            return true;
        }

        if (key.KeyChar == '?')
        {
            Hide();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Single source of truth for the centered box: content-measured width
    /// clamped to <c>MinWidth..MaxWidth</c> and the viewport, height from the
    /// truncated line count. Never escapes the viewport.
    /// </summary>
    public Rect ComputeBox(Rect viewport)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return default;
        }

        int longest = 0;
        foreach (string line in BuildLines(int.MaxValue))
        {
            if (line.Length > longest)
            {
                longest = line.Length;
            }
        }

        int availW = Math.Max(0, viewport.Width - 2);
        int availH = Math.Max(0, viewport.Height - 2);
        int width = Math.Min(Math.Clamp(longest + Chrome, MinWidth, MaxWidth), Math.Max(1, availW));
        int innerW = Math.Max(0, width - Chrome);
        int count = BuildLines(innerW).Count;

        int height = Math.Min(Math.Clamp(count + 2, MinHeight, MaxHeight), Math.Max(1, availH));
        int x = viewport.X + Math.Max(0, (viewport.Width - width) / 2);
        int y = viewport.Y + Math.Max(0, (viewport.Height - height) / 2);
        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// Paints the modal centered inside <paramref name="viewport"/> (typically
    /// the full screen). No-op when hidden or the box drops below minimums.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect viewport)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!Visible)
        {
            return;
        }

        var box = ComputeBox(viewport);
        if (box.Width < MinWidth || box.Height < MinHeight)
        {
            return;
        }

        if (box.X >= buffer.Cols || box.Y >= buffer.Rows)
        {
            return;
        }

        PanelChrome.PaintBorderBox(buffer, box);

        int innerW = box.Width - Chrome;
        var lines = BuildLines(innerW);
        int rows = Math.Min(lines.Count, box.Height - 2);
        for (int i = 0; i < rows; i++)
        {
            buffer.SetText(box.X + 2, box.Y + 1 + i, lines[i], StyleFor(i, lines[i]));
        }
    }

    internal List<string> BuildLines(int innerWidth)
    {
        var lines = new List<string>(HelpKeymap.Rows.Count + 8);
        lines.Add("Which-key — hotkeys");
        lines.Add(new string('─', Math.Clamp(innerWidth == int.MaxValue ? 48 : innerWidth, 8, 48)));
        lines.Add("Hotkeys");
        foreach (HelpKeymap.Entry hotkey in HelpKeymap.Rows)
        {
            lines.Add($"  {hotkey.Key,-12} {hotkey.Description}");
        }

        if (HasContext)
        {
            lines.Add(string.Empty);
            lines.Add("Context");
            if (!string.IsNullOrEmpty(_context?.FocusedPanelId))
            {
                lines.Add($"  focus   {_context.FocusedPanelId}");
            }

            if (!string.IsNullOrEmpty(_context?.ActiveOverlayId))
            {
                lines.Add($"  overlay {_context.ActiveOverlayId}");
            }
        }

        lines.Add(string.Empty);
        lines.Add("Press ? to close.");

        if (innerWidth != int.MaxValue)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Length > innerWidth)
                {
                    lines[i] = lines[i][..innerWidth];
                }
            }
        }

        return lines;
    }

    private static CellStyle StyleFor(int index, string line)
    {
        if (index == 0)
        {
            return new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        }

        if (line is "Hotkeys" or "Context")
        {
            return new CellStyle(ChatPalette.Text, attrs: StyleAttr.Bold);
        }

        if (line.Length == 0 || line[0] == '─')
        {
            return ChatPalette.Dim;
        }

        return new CellStyle(ChatPalette.Text);
    }
}
