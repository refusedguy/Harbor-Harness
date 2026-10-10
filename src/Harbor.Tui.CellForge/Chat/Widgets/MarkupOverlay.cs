using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Screenshot-markup overlay (KILLER_FEATURES §2.7 Feature 14, issue #400
/// slice 1/2): keyboard- and mouse-driven annotation over the image row the
/// viewer opened, with the session living in the store, not here.
/// <para><b>Contract.</b> Modal and opaque — it occludes the chat beneath it
/// and forms an input barrier, so keys never reach the agent while it is open:
/// the host checks <see cref="Visible" /> and calls <see cref="HandleKey" />
/// directly in <c>ReplInputLoop.HandleKeyAsync</c>, then swallows the rest.
/// That is the same object <see cref="MarkupOverlayLayer" /> wraps — the
/// layer takes the <c>OnKey</c> default, exactly like the dialog and toast
/// layers, so nothing about today's behaviour depends on the stack being the
/// router. <c>Sync</c> adopts the store snapshot each frame; the overlay keeps
/// no session of its own, so closing it restores the feed by construction —
/// the host re-applies the scroll the state snapshotted at open.</para>
/// <para><b>Honest wireframe.</b> The overlay does not repaint the source
/// bitmap (paint-time file I/O is forbidden, and the store carries paths, not
/// bytes). It draws the aspect-fitted image box plus the annotations as cell
/// wireframes over it; the bake step composites the same model onto the real
/// pixels. A live-bitmap backdrop is slice-2 work.</para>
/// </summary>
public sealed class MarkupOverlay
{
    /// <summary>Smallest viewport the overlay will paint into.</summary>
    public const int MinWidth = 20;

    /// <summary>Smallest viewport height the overlay will paint into.</summary>
    public const int MinHeight = 6;

    /// <summary>Keyboard nudge step in unit space (arrows move / resize).</summary>
    public const double KeyStep = 0.05;

    /// <summary>Rows the header + footer chrome costs inside the box.</summary>
    private const int ChromeRows = 5;

    private MarkupOverlayState _snapshot = MarkupOverlayState.Closed;

    /// <summary>Store snapshot adopted on the last <see cref="Sync" /> (closed until the first open).</summary>
    public MarkupOverlayState Snapshot => _snapshot;

    /// <summary>Whether the overlay is up and must occlude the chat.</summary>
    public bool Visible => _snapshot.IsOpen;

    /// <summary>Adopts the store snapshot. Called every frame; the overlay never edits it.</summary>
    public void Sync(MarkupOverlayState snapshot) => _snapshot = snapshot ?? MarkupOverlayState.Closed;

    /// <summary>
    /// Routes a decoded key to a store message. Null means "not a markup
    /// gesture" — the host still swallows the key (the barrier contract:
    /// panels beneath starve) but dispatches nothing. Release events are
    /// never gestures. Ctrl+S is the host's save chord and is deliberately
    /// NOT claimed here: the reducer is pure, so saving stays a host effect
    /// (see the save path in the REPL loop).
    /// </summary>
    public AppMsg? HandleKey(in KeyEvent key)
    {
        if (!Visible)
        {
            return null;
        }

        if (key.EventType == KeyEventType.Release)
        {
            return null;
        }

        var markup = _snapshot;
        switch (key.Key)
        {
            case KeyCode.Escape:
                return new ChatAppMsg.CloseMarkup();
            case KeyCode.Enter:
                return new ChatAppMsg.MarkupPlace();
            case KeyCode.Tab when key.Modifiers.IsUnmodified():
                return new ChatAppMsg.MarkupSelectNext();
            case KeyCode.Up:
                return ShiftDown(key.Modifiers)
                    ? new ChatAppMsg.MarkupResize(0, -KeyStep)
                    : MoveOrCursor(0, -KeyStep);
            case KeyCode.Down:
                return ShiftDown(key.Modifiers)
                    ? new ChatAppMsg.MarkupResize(0, KeyStep)
                    : MoveOrCursor(0, KeyStep);
            case KeyCode.Left:
                return ShiftDown(key.Modifiers)
                    ? new ChatAppMsg.MarkupResize(-KeyStep, 0)
                    : MoveOrCursor(-KeyStep, 0);
            case KeyCode.Right:
                return ShiftDown(key.Modifiers)
                    ? new ChatAppMsg.MarkupResize(KeyStep, 0)
                    : MoveOrCursor(KeyStep, 0);
            case KeyCode.Delete:
                return new ChatAppMsg.MarkupDeleteSelected();
            case KeyCode.Backspace:
                return markup.PendingText.Length > 0 && markup.ActiveTool == MarkupKind.Text
                    ? new ChatAppMsg.MarkupSetPendingText(markup.PendingText[..^1])
                    : new ChatAppMsg.MarkupDeleteSelected();
            case KeyCode.Char:
                return HandleChar(key, markup);
            default:
                return null;
        }
    }

    /// <summary>
    /// Routes a mouse event to a store message using the current viewport's
    /// image box. Null when the overlay is closed, the button is not left, or
    /// the cell falls outside the image box — the host still swallows while
    /// open. Wheel never annotates.
    /// </summary>
    public AppMsg? HandleMouse(in MouseEvent mouse, Rect viewport)
    {
        if (!Visible || mouse.Button != MouseButton.Left)
        {
            return null;
        }

        var box = ComputeBox(viewport);
        var image = ComputeImageRect(box);
        if (image.Width <= 1 || image.Height <= 1)
        {
            return null;
        }

        if (!image.Contains(mouse.Column, mouse.Row))
        {
            return null;
        }

        double x = (double)(mouse.Column - image.X) / (image.Width - 1);
        double y = (double)(mouse.Row - image.Y) / (image.Height - 1);
        return mouse.Type switch
        {
            MouseEventType.Press => new ChatAppMsg.MarkupPressAt(x, y),
            MouseEventType.Click => new ChatAppMsg.MarkupSelectAt(x, y),
            MouseEventType.Drag => new ChatAppMsg.MarkupDragTo(x, y),
            MouseEventType.Release => new ChatAppMsg.MarkupReleaseAt(x, y),
            _ => null,
        };
    }

    /// <summary>
    /// Fullscreen box: the whole viewport when it fits the minimums, default
    /// (empty) otherwise. Single source of truth for <see cref="Paint" /> and
    /// the layer seating.
    /// </summary>
    public Rect ComputeBox(Rect viewport)
    {
        if (viewport.Width < MinWidth || viewport.Height < MinHeight)
        {
            return default;
        }

        return new Rect(viewport.X, viewport.Y, viewport.Width, viewport.Height);
    }

    /// <summary>
    /// The cell box the source image maps into: aspect-fitted (cell aspect
    /// <see cref="ImageBlock.CellAspect" />) into the body area, centred.
    /// Pure, so the paint test and the mouse mapping share it.
    /// </summary>
    public Rect ComputeImageRect(Rect box)
    {
        int availW = box.Width - 2;
        int bodyH = Math.Max(0, box.Height - ChromeRows);
        if (availW <= 0 || bodyH <= 0)
        {
            return default;
        }

        int cols = availW;
        int rows = bodyH;
        if (_snapshot.SourceWidth > 0 && _snapshot.SourceHeight > 0)
        {
            double want = cols * ((double)_snapshot.SourceHeight / _snapshot.SourceWidth) / ImageBlock.CellAspect;
            rows = (int)Math.Round(want, MidpointRounding.AwayFromZero);
            if (rows > bodyH)
            {
                rows = bodyH;
                cols = Math.Max(1, (int)Math.Round(rows * ((double)_snapshot.SourceWidth / _snapshot.SourceHeight) * ImageBlock.CellAspect, MidpointRounding.AwayFromZero));
                cols = Math.Min(cols, availW);
            }

            rows = Math.Clamp(rows, 1, bodyH);
            cols = Math.Clamp(cols, 1, availW);
        }

        int x = box.X + 1 + ((availW - cols) / 2);
        int y = box.Y + 2 + ((bodyH - rows) / 2);
        return new Rect(x, y, cols, rows);
    }

    /// <summary>Paints the wireframe overlay: frame, image box, annotations, cursor, footer.</summary>
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

        var markup = _snapshot;
        int innerW = box.Width - 2;

        buffer.Fill(box, Cell.Blank);
        PanelChrome.PaintBorderBox(buffer, box, BoxStyle.SquareFrame);

        string dims = markup.SourceWidth > 0 ? $"{markup.SourceWidth}×{markup.SourceHeight}" : "no dims";
        string header = $"{markup.SourceName}  {dims}  [{ToolName(markup.ActiveTool)}] {markup.Model.Items.Length} ann";
        buffer.SetText(box.X + 1, box.Y + 1, Truncate(header, innerW),
            new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold));

        var image = ComputeImageRect(box);
        if (image.Width > 0 && image.Height > 0)
        {
            PaintImageFrame(buffer, image);
            PaintCursor(buffer, image, markup.Cursor);
            foreach (MarkupAnnotation item in markup.Model.Items)
            {
                PaintAnnotation(buffer, image, item, selected: item.Id == markup.Model.SelectedId);
            }

            if (markup.Draft is { } draft)
            {
                PaintDraft(buffer, image, markup.ActiveTool, draft);
            }
        }

        int footerY = box.Bottom - 2;
        if (!string.IsNullOrEmpty(markup.Error))
        {
            buffer.SetText(box.X + 1, footerY - 2, Truncate("! " + markup.Error, innerW), ChatPalette.ToolError);
        }
        else if (!string.IsNullOrEmpty(markup.SavedPath))
        {
            buffer.SetText(box.X + 1, footerY - 2, Truncate("saved: " + SavedName(markup.SavedPath), innerW), ChatPalette.ToolOk);
        }

        if (markup.ActiveTool == MarkupKind.Text)
        {
            buffer.SetText(box.X + 1, footerY - 1, Truncate("Text: " + markup.PendingText + "|", innerW), ChatPalette.ToolArgs);
        }

        const string Hints = "1/2/3|space|arrows move|S+arrows resize|u undo|^R redo|del|^S save|esc close";
        buffer.SetText(box.X + 1, footerY, Truncate(Hints, innerW), ChatPalette.Dim);
    }

    private AppMsg? HandleChar(in KeyEvent key, MarkupOverlayState markup)
    {
        if (key.Modifiers.IsCommandModifier())
        {
            // Ctrl+R redoes; every other command chord belongs to the host keymap.
            string chord = key.Character.ToString();
            if ((key.Modifiers & KeyModifiers.Ctrl) != 0 && (chord == "r" || chord == "R"))
            {
                return new ChatAppMsg.MarkupRedo();
            }

            return null;
        }

        string text = key.Character.ToString();
        switch (text)
        {
            case "u":
                return new ChatAppMsg.MarkupUndo();
            case "q" when markup.ActiveTool != MarkupKind.Text:
                // Second dismiss gesture (the viewer closes on q too); while
                // the text tool is armed every printable feeds the buffer.
                return new ChatAppMsg.CloseMarkup();
            case "1":
                return new ChatAppMsg.MarkupSelectTool(MarkupKind.Arrow);
            case "2":
                return new ChatAppMsg.MarkupSelectTool(MarkupKind.Rectangle);
            case "3":
                return new ChatAppMsg.MarkupSelectTool(MarkupKind.Text);
            case "n":
            case " ":
                return new ChatAppMsg.MarkupPlace();
            default:
                if (markup.ActiveTool == MarkupKind.Text && IsTypable(key.Character))
                {
                    return new ChatAppMsg.MarkupSetPendingText(markup.PendingText + text);
                }

                return null;
        }
    }

    private AppMsg MoveOrCursor(double dx, double dy) =>
        _snapshot.Model.SelectedId is not null
            ? new ChatAppMsg.MarkupNudge(dx, dy)
            : new ChatAppMsg.MarkupMoveCursor(dx, dy);

    private static bool ShiftDown(KeyModifiers modifiers) => (modifiers & KeyModifiers.Shift) != 0;

    private static bool IsTypable(Rune rune)
    {
        if (Rune.IsControl(rune))
        {
            return false;
        }

        // DEL and the C1 block are not text even though some land in Char keys.
        int value = rune.Value;
        return value != 0x7F && (value < 0x80 || value >= 0xA0);
    }

    private static string ToolName(MarkupKind tool) => tool switch
    {
        MarkupKind.Arrow => "arrow",
        MarkupKind.Rectangle => "rect",
        _ => "text",
    };

    private static string SavedName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "?";
        }

        int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static void PaintImageFrame(ScreenBuffer buffer, Rect image)
    {
        // Faint ASCII frame so the fitted box reads as "the picture goes
        // here" even on terminals without a graphics protocol.
        for (int x = image.X; x < image.Right; x++)
        {
            Plot(buffer, image, x, image.Y, '-', PanelChrome.BorderStyle);
            Plot(buffer, image, x, image.Bottom - 1, '-', PanelChrome.BorderStyle);
        }

        for (int y = image.Y; y < image.Bottom; y++)
        {
            Plot(buffer, image, image.X, y, '|', PanelChrome.BorderStyle);
            Plot(buffer, image, image.Right - 1, y, '|', PanelChrome.BorderStyle);
        }

        Plot(buffer, image, image.X, image.Y, '+', PanelChrome.BorderStyle);
        Plot(buffer, image, image.Right - 1, image.Y, '+', PanelChrome.BorderStyle);
        Plot(buffer, image, image.X, image.Bottom - 1, '+', PanelChrome.BorderStyle);
        Plot(buffer, image, image.Right - 1, image.Bottom - 1, '+', PanelChrome.BorderStyle);
    }

    private static void PaintAnnotation(ScreenBuffer buffer, Rect image, MarkupAnnotation item, bool selected)
    {
        var style = selected
            ? new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold)
            : new CellStyle(ChatPalette.Text);
        switch (item.Kind)
        {
            case MarkupKind.Rectangle:
                PaintRect(buffer, image, item.From, item.To, style);
                break;
            case MarkupKind.Arrow:
                PaintArrow(buffer, image, item.From, item.To, style);
                break;
            default:
                PaintText(buffer, image, item.From, item.Text, selected ? style : ChatPalette.ToolArgs);
                break;
        }
    }

    private static void PaintDraft(ScreenBuffer buffer, Rect image, MarkupKind tool, MarkupDraft draft)
    {
        switch (tool)
        {
            case MarkupKind.Rectangle:
                PaintRect(buffer, image, draft.Anchor, draft.Current, ChatPalette.Dim);
                break;
            case MarkupKind.Arrow:
                PaintArrow(buffer, image, draft.Anchor, draft.Current, ChatPalette.Dim);
                break;
            default:
                break;
        }
    }

    private static void PaintRect(ScreenBuffer buffer, Rect image, NormalizedPoint a, NormalizedPoint b, CellStyle style)
    {
        var (x0, y0) = MarkupAnnotationModel.ProjectToCells(a, image);
        var (x1, y1) = MarkupAnnotationModel.ProjectToCells(b, image);
        int left = Math.Min(x0, x1);
        int right = Math.Max(x0, x1);
        int top = Math.Min(y0, y1);
        int bottom = Math.Max(y0, y1);

        for (int x = left; x <= right; x++)
        {
            Plot(buffer, image, x, top, '─', style);
            Plot(buffer, image, x, bottom, '─', style);
        }

        for (int y = top; y <= bottom; y++)
        {
            Plot(buffer, image, left, y, '│', style);
            Plot(buffer, image, right, y, '│', style);
        }

        // Corners come from the single spelling (PanelChrome.CornersFor), so
        // the one-file rule on box-drawing corners keeps holding.
        ReadOnlySpan<char> corners = PanelChrome.CornersFor(BoxStyle.SquareFrame);
        Plot(buffer, image, left, top, corners[0], style);
        Plot(buffer, image, right, top, corners[1], style);
        Plot(buffer, image, left, bottom, corners[2], style);
        Plot(buffer, image, right, bottom, corners[3], style);
    }

    private static void PaintArrow(ScreenBuffer buffer, Rect image, NormalizedPoint a, NormalizedPoint b, CellStyle style)
    {
        var (x0, y0) = MarkupAnnotationModel.ProjectToCells(a, image);
        var (x1, y1) = MarkupAnnotationModel.ProjectToCells(b, image);
        if (x0 == x1 && y0 == y1)
        {
            Plot(buffer, image, x0, y0, 'o', style);
            return;
        }

        int dx = x1 - x0;
        int dy = y1 - y0;
        int steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
        for (int i = 0; i <= steps; i++)
        {
            int x = x0 + ((dx * i) / steps);
            int y = y0 + ((dy * i) / steps);
            if (x == x1 && y == y1)
            {
                continue;
            }

            Plot(buffer, image, x, y, ShaftFor(dx, dy), style);
        }

        Plot(buffer, image, x1, y1, HeadFor(dx, dy), style);
    }

    private static char ShaftFor(int dx, int dy)
    {
        int ax = Math.Abs(dx);
        int ay = Math.Abs(dy);
        if (ax > ay * 2)
        {
            return '─';
        }

        if (ay > ax * 2)
        {
            return '│';
        }

        return (dx < 0) == (dy < 0) ? '╲' : '╱';
    }

    private static char HeadFor(int dx, int dy)
    {
        int ax = Math.Abs(dx);
        int ay = Math.Abs(dy);
        if (ax > ay * 2)
        {
            return dx > 0 ? '▶' : '◀';
        }

        if (ay > ax * 2)
        {
            return dy > 0 ? '▼' : '▲';
        }

        if (dx > 0)
        {
            return dy > 0 ? '↘' : '↗';
        }

        return dy > 0 ? '↙' : '↖';
    }

    private static void PaintText(ScreenBuffer buffer, Rect image, NormalizedPoint anchor, string text, CellStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var (x, y) = MarkupAnnotationModel.ProjectToCells(anchor, image);
        int max = Math.Min(text.Length, image.Right - x);
        for (int i = 0; i < max; i++)
        {
            Plot(buffer, image, x + i, y, text[i], style);
        }
    }

    private static void PaintCursor(ScreenBuffer buffer, Rect image, NormalizedPoint cursor)
    {
        var (x, y) = MarkupAnnotationModel.ProjectToCells(cursor, image);
        Plot(buffer, image, x, y, '+', new CellStyle(ChatPalette.Warning, attrs: StyleAttr.Bold));
    }

    private static void Plot(ScreenBuffer buffer, Rect image, int x, int y, char glyph, CellStyle style)
    {
        if (!image.Contains(x, y) || x < 0 || y < 0 || x >= buffer.Cols || y >= buffer.Rows)
        {
            return;
        }

        buffer.SetRune(x, y, new Rune(glyph), style);
    }

    private static string Truncate(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(0, width)];
}
