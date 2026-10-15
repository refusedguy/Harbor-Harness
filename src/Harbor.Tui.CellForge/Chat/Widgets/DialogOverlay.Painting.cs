using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Tui.CellForge.Widgets;

public sealed partial class DialogOverlay
{
    /// <summary>
    /// Computes the centered modal box for <paramref name="viewport"/> (typically
    /// the full screen). Returns default when hidden or the viewport is too small.
    /// Single source of truth for <see cref="Paint"/> and the PRIM2c overlay-layer
    /// seating (<see cref="DialogOverlayLayer"/> reports this as its bounds).
    /// </summary>
    public Rect ComputeBox(Rect viewport)
    {
        if (!Visible || viewport.Width < MinWidth || viewport.Height < MinHeight)
        {
            return default;
        }

        int width = Math.Min(MaxWidth, viewport.Width - 2);
        int contentRows = CountMessageRows(width) + ButtonRowHeight + (Padding * 2) + ControlRows();
        int height = Math.Min(MaxHeight, Math.Max(MinHeight, Math.Min(viewport.Height - 2, contentRows + 2)));
        int x = viewport.X + (viewport.Width - width) / 2;
        int y = viewport.Y + (viewport.Height - height) / 2;
        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// Paint the modal centered inside <paramref name="rect"/> (typically the
    /// full screen). No-op when hidden or the rect is too small.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        if (!Visible || rect.Width < MinWidth || rect.Height < MinHeight)
        {
            return;
        }
        if (rect.X >= buffer.Cols || rect.Y >= buffer.Rows)
        {
            return;
        }

        var box = ComputeBox(rect);
        if (box.Width <= 0 || box.Height <= 0)
        {
            return;
        }
        PanelChrome.PaintBorderBox(buffer, box);

        int textX = box.X + Padding;
        int textY = box.Y + Padding;
        int innerW = box.Width - (Padding * 2);
        DrawTitle(buffer, textX, textY, innerW);
        textY += 1;

        int controlRows = ControlRows();
        int messageRows = Math.Max(1, box.Height - (Padding * 2) - 2 - ButtonRowHeight - controlRows);
        string[] wrapped = WrapText(_message, innerW);
        int drawn = 0;
        for (int i = 0; i < wrapped.Length && drawn < messageRows; i++)
        {
            buffer.SetText(textX, textY + drawn, wrapped[i].AsSpan(0, Math.Min(wrapped[i].Length, innerW)), ChatPalette.Dim);
            drawn++;
        }
        textY += messageRows;

        if (_kind == DialogKind.Prompt)
        {
            buffer.SetText(textX, textY, "› ", new CellStyle(ChatPalette.Accent));
            string input = _input.Length > innerW - 2 ? _input[(^Math.Max(1, innerW - 2))..] : _input;
            buffer.SetText(textX + 2, textY, input, new CellStyle(ChatPalette.Accent));
        }
        else if (_kind == DialogKind.Select)
        {
            PaintSelectList(buffer, box, textX, textY, innerW);
        }
        else if (_kind == DialogKind.Radio)
        {
            PaintRadioRow(buffer, textX, textY, innerW, box);
        }
        else if (_kind == DialogKind.Multiline)
        {
            PaintMultiline(buffer, textX, textY, innerW, box);
        }
        else if (_kind == DialogKind.Approval)
        {
            PaintApproval(buffer, box, textX, textY, innerW);
        }

        DrawButtons(buffer, textX, box.Bottom - ButtonRowHeight - 1, innerW);
    }

    /// <summary>
    /// Paints the [UX7] approval body below the message: optional file-path +
    /// capped unified-diff preview (PRIM12 palette mapping), the fixed PRIM4
    /// choice row, then the reject-reason caption + capped editor rows.
    /// Every section clips at the button row, so small boxes degrade by
    /// truncation, never by overlap.
    /// </summary>
    private void PaintApproval(ScreenBuffer buffer, Rect box, int x, int y, int innerW)
    {
        int bottom = box.Bottom - ButtonRowHeight - 1;
        if (_approvalDiff.Count > 0)
        {
            if (_approvalFile.Length > 0 && y < bottom)
            {
                string path = _approvalFile.Length > innerW
                    ? "…" + _approvalFile[^Math.Max(1, innerW - 1)..]
                    : _approvalFile;
                buffer.SetText(x, y, path, new CellStyle(ChatPalette.Muted));
                y++;
            }
            for (int i = 0; i < _approvalDiff.Count && y < bottom; i++)
            {
                var line = _approvalDiff[i];
                string prefix = line.Kind switch
                {
                    DiffViewerLineKind.Added => "+ ",
                    DiffViewerLineKind.Removed => "- ",
                    _ => "  ",
                };
                var style = line.Kind switch
                {
                    DiffViewerLineKind.Added => ChatPalette.ToolOk,
                    DiffViewerLineKind.Removed => ChatPalette.ToolError,
                    _ => ChatPalette.ToolBody,
                };
                string row = prefix + line.Text;
                if (row.Length > innerW)
                {
                    row = row[..Math.Max(0, innerW - 1)] + "…";
                }
                buffer.SetText(x, y, row, style);
                y++;
            }
        }
        if (y < bottom)
        {
            PaintRadioRow(buffer, x, y, innerW, box);
            y++;
        }
        if (y < bottom)
        {
            string caption = ApprovalReasonCaption.Length > innerW
                ? ApprovalReasonCaption[..Math.Max(0, innerW - 1)] + "…"
                : ApprovalReasonCaption;
            buffer.SetText(x, y, caption, new CellStyle(ChatPalette.Muted));
            y++;
        }
        string[] reason = _editor.Text.Split('\n');
        var caretStyle = new CellStyle(ChatPalette.Accent);
        for (int i = 0; i < reason.Length && i < MaxApprovalReasonRows && y < bottom; i++)
        {
            string row = (i == 0 ? "› " : "  ") + reason[i];
            if (row.Length > innerW)
            {
                row = row[..Math.Max(0, innerW - 1)] + "…";
            }
            buffer.SetText(x, y, row, i == 0 ? caretStyle : ChatPalette.ToolArgs);
            y++;
        }
    }

    /// <summary>
    /// Control-block height: prompt/editor/select/radio rows below the message.
    /// Alert/Confirm reserve none, so their boxes are unchanged by [PRIM4].
    /// </summary>
    private int ControlRows() => _kind switch
    {
        DialogKind.Prompt => 1,
        DialogKind.Select => Math.Min(_select.Count, MaxSelectRows),
        DialogKind.Radio => _radio.Count > 0 ? 1 : 0,
        DialogKind.Multiline => Math.Clamp(_editor.LineCount, 1, MaxMultilineRows),
        DialogKind.Approval => ApprovalControlRows(),
        _ => 0,
    };

    /// <summary>
    /// Control-block height of the [UX7] approval modal: diff section
    /// (file-path line when set + capped preview rows) + choice row +
    /// reason caption + capped reason rows. Mirrors <see cref="PaintApproval"/>
    /// section order so <see cref="ComputeBox"/> reserves exactly what paint emits.
    /// </summary>
    private int ApprovalControlRows()
    {
        int rows = 0;
        if (_approvalDiff.Count > 0)
        {
            rows += (_approvalFile.Length > 0 ? 1 : 0) + _approvalDiff.Count;
        }
        rows += 1; // fixed PRIM4 choice row
        rows += 1; // reject-reason caption
        rows += Math.Clamp(_editor.LineCount, 1, MaxApprovalReasonRows);
        return rows;
    }

    private void PaintSelectList(ScreenBuffer buffer, Rect box, int x, int y, int innerW)
    {
        int available = Math.Max(0, box.Bottom - ButtonRowHeight - 1 - y);
        if (available <= 0 || _select.Count == 0)
        {
            return;
        }
        // Row painting lives in the widget (#1158); the legacy selection API
        // keeps this call byte-identical to the inline version it replaces.
        _select.Paint(buffer, new Rect(x, y, innerW, available));
    }

    private void PaintRadioRow(ScreenBuffer buffer, int x, int y, int innerW, Rect box)
    {
        if (_radio.Count == 0 || y >= box.Bottom - ButtonRowHeight - 1)
        {
            return;
        }
        int selected = _radio.SelectedIndex;
        var onStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var offStyle = new CellStyle(ChatPalette.Muted);
        // Two-tone pass: selected option accented, the rest muted.
        int cursor = x;
        for (int i = 0; i < _radio.Count && cursor < x + innerW; i++)
        {
            string chip = "(" + (i == selected ? "● " : "○ ") + _radio.Options[i] + ")";
            if (i > 0)
            {
                if (cursor >= x + innerW)
                {
                    break;
                }
                buffer.SetText(cursor, y, " ", offStyle);
                cursor++;
            }
            int len = Math.Min(chip.Length, x + innerW - cursor);
            if (len <= 0)
            {
                break;
            }
            buffer.SetText(cursor, y, chip.AsSpan(0, len), i == selected ? onStyle : offStyle);
            cursor += len;
        }
    }

    private void PaintMultiline(ScreenBuffer buffer, int x, int y, int innerW, Rect box)
    {
        int available = Math.Max(0, box.Bottom - ButtonRowHeight - 1 - y);
        if (available <= 0)
        {
            return;
        }
        string[] lines = _editor.Text.Split('\n');
        var caretStyle = new CellStyle(ChatPalette.Accent);
        for (int i = 0; i < lines.Length && i < available; i++)
        {
            string row = (i == 0 ? "› " : "  ") + lines[i];
            if (row.Length > innerW)
            {
                row = row[..Math.Max(0, innerW - 1)] + "…";
            }
            buffer.SetText(x, y + i, row, i == 0 ? caretStyle : ChatPalette.ToolArgs);
        }
    }

    private void DrawTitle(ScreenBuffer buffer, int x, int y, int innerW)
    {
        string title = _title.Length > innerW ? _title[..Math.Max(0, innerW - 1)] + "…" : _title;
        buffer.SetText(x, y, title, new CellStyle(ChatPalette.Accent));
    }

    private void DrawButtons(ScreenBuffer buffer, int x, int y, int innerW)
    {
        if (_buttons.Count == 0)
        {
            return;
        }
        int span = _buttons.Count;
        int gap = 2;
        int total = 0;
        for (int i = 0; i < span; i++)
        {
            total += _buttons[i].Label.Length + 2;
        }
        total += (span - 1) * gap;
        int startX = x + Math.Max(0, (innerW - total) / 2);
        int cursor = startX;
        for (int i = 0; i < span; i++)
        {
            var button = _buttons[i];
            var style = i == _focusedButton ? new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold) : new CellStyle(ChatPalette.Muted);
            buffer.SetText(cursor, y, "[", style);
            buffer.SetText(cursor + 1, y, button.Label, style);
            buffer.SetText(cursor + 1 + button.Label.Length, y, "]", style);
            cursor += button.Label.Length + 2 + gap;
        }
    }

    private int CountMessageRows(int innerW)
    {
        if (innerW <= 0)
        {
            return 1;
        }
        return Math.Max(1, WrapText(_message, innerW).Length);
    }

    private static string[] WrapText(string text, int width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return [string.Empty];
        }
        var lines = new List<string>();
        int pos = 0;
        while (pos < text.Length)
        {
            int len = Math.Min(width, text.Length - pos);
            int breakAt = -1;
            for (int i = pos + len - 1; i > pos; i--)
            {
                if (text[i] == ' ' || text[i] == '\n')
                {
                    breakAt = i;
                    break;
                }
            }
            if (breakAt < pos)
            {
                lines.Add(text.Substring(pos, len));
                pos += len;
            }
            else
            {
                lines.Add(text.Substring(pos, breakAt - pos));
                pos = breakAt + 1;
            }
        }
        return lines.Count == 0 ? [string.Empty] : lines.ToArray();
    }
}
