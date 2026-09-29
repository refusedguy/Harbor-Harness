using CSharpFunctionalExtensions;
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

    // ONE source of truth for "no context was supplied". Was a bare `WhichKeyContext?`
    // that three members each re-derived from, so `Show(null)` produced a shown
    // overlay whose Context was null while HasContext asked a different question
    // (#592). Maybe makes the absence explicit at every read, and `Context` below is
    // the only projection of it — HasContext and BuildLines both read through that.
    private Maybe<WhichKeyContext> _context;

    private bool _shown;

    /// <summary>
    ///     The single authority on "did the host show this overlay?". The layer's
    ///     <see cref="WhichKeyHelpOverlayLayer.Visible" /> used to be a second,
    ///     independent answer (shown AND big enough to fit), so a shown overlay
    ///     reported <c>Visible == false</c> on a small terminal and the host could not
    ///     tell a geometry problem from a state problem. That is now
    ///     <see cref="WhichKeyHelpOverlayLayer.HasRoom" />, named for what it answers.
    /// </summary>
    public bool IsShown => _shown;

    /// <summary>Alias of <see cref="IsShown" />, kept for the existing caller surface.</summary>
    public bool Visible => _shown;

    /// <summary>
    ///     Host-supplied context. <see cref="Maybe{T}.None" /> when the host showed the
    ///     overlay with no context — a real, expected state, not a missing value.
    /// </summary>
    public Maybe<WhichKeyContext> Context => _context;

    /// <summary>
    ///     True when the context section has something to print: a context was supplied
    ///     AND it names a focused panel or an active overlay. Derived from
    ///     <see cref="Context" /> rather than re-deriving the lookup.
    /// </summary>
    public bool HasContext
    {
        get
        {
            Maybe<WhichKeyContext> context = Context;
            if (context.HasNoValue)
            {
                return false;
            }

            WhichKeyContext value = context.Value;
            return !string.IsNullOrEmpty(value.FocusedPanelId) ||
                   !string.IsNullOrEmpty(value.ActiveOverlayId);
        }
    }

    public void Show(WhichKeyContext? context = null)
    {
        _context = Maybe.From(context);
        _shown = true;
    }

    /// <summary>
    ///     Dismiss. The context is deliberately NOT cleared: a host primes it with
    ///     <see cref="SetContext" /> while hidden and then hands it to
    ///     <see cref="Show(WhichKeyContext?)" />. The shown flag is the state; the context
    ///     is content. Note that <see cref="Show(WhichKeyContext?)" /> assigns its argument
    ///     unconditionally, so calling it with no argument clears the primed context — that
    ///     is pre-existing behaviour, not something this wave changed.
    /// </summary>
    public void Hide()
    {
        _shown = false;
    }

    public void SetContext(WhichKeyContext? context) => _context = Maybe.From(context);

    /// <summary>
    /// Overlay-local dismissal (Esc / '?'), mirroring
    /// <c>DialogOverlay.HandleKey</c>. Anything else passes through untouched —
    /// chord resolution stays with the host keymap.
    /// </summary>
    public bool HandleKey(ConsoleKeyInfo key)
    {
        if (!IsShown)
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
        if (!IsShown)
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

        // Read through Context, not the backing field: the render path and the public
        // surface now look at one value, so a future state change cannot leave them
        // disagreeing about whether a context exists.
        Maybe<WhichKeyContext> context = Context;
        if (context.HasValue)
        {
            WhichKeyContext value = context.Value;
            bool hasFocus = !string.IsNullOrEmpty(value.FocusedPanelId);
            bool hasOverlay = !string.IsNullOrEmpty(value.ActiveOverlayId);
            if (hasFocus || hasOverlay)
            {
                lines.Add(string.Empty);
                lines.Add("Context");
                if (hasFocus)
                {
                    lines.Add($"  focus   {value.FocusedPanelId}");
                }

                if (hasOverlay)
                {
                    lines.Add($"  overlay {value.ActiveOverlayId}");
                }
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
