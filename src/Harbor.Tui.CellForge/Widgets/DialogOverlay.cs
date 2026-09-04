using Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Modal dialog kind. Drives button layout and default Enter behaviour.
/// </summary>
public enum DialogKind
{
    Alert,
    Confirm,
    Prompt,
}

/// <summary>
/// One button on a <see cref="DialogOverlay"/>.
/// </summary>
public sealed record DialogButton(string Label, string Id);

/// <summary>
/// Cell-native modal dialog overlay (CellForge EPIC H).
/// Hosts a single centered modal box on top of the chat feed.
/// Hosts are responsible for advancing <see cref="Tick"/> for animations
/// (none today; the field is reserved for future spinner integration) and
/// for invoking <see cref="Dismiss"/> after a button commits.
/// </summary>
public sealed class DialogOverlay
{
    public const int MinWidth = 20;
    public const int MaxWidth = 72;
    public const int MinHeight = 5;
    public const int MaxHeight = 18;
    private const int Padding = 1;
    private const int ButtonRowHeight = 2;

    private readonly List<DialogButton> _buttons = new();
    private string _title = string.Empty;
    private string _message = string.Empty;
    private string _input = string.Empty;
    private int _focusedButton;
    private DialogKind _kind = DialogKind.Alert;

    public DialogOverlay()
    {
    }

    public bool Visible { get; private set; }

    public string Title => _title;

    public string Message => _message;

    public string Input => _input;

    public IReadOnlyList<DialogButton> Buttons => _buttons;

    public int FocusedButtonIndex => _focusedButton;

    public DialogKind Kind => _kind;

    public void ShowAlert(string title, string message, string okLabel = "OK")
    {
        ArgumentNullException.ThrowIfNull(okLabel);
        _kind = DialogKind.Alert;
        _title = title ?? string.Empty;
        _message = message ?? string.Empty;
        _input = string.Empty;
        _buttons.Clear();
        _buttons.Add(new DialogButton(okLabel, "ok"));
        _focusedButton = 0;
        Visible = true;
    }

    public void ShowConfirm(
        string title,
        string message,
        string okLabel = "OK",
        string cancelLabel = "Cancel")
    {
        ArgumentNullException.ThrowIfNull(okLabel);
        ArgumentNullException.ThrowIfNull(cancelLabel);
        _kind = DialogKind.Confirm;
        _title = title ?? string.Empty;
        _message = message ?? string.Empty;
        _input = string.Empty;
        _buttons.Clear();
        _buttons.Add(new DialogButton(okLabel, "ok"));
        _buttons.Add(new DialogButton(cancelLabel, "cancel"));
        _focusedButton = 0;
        Visible = true;
    }

    public void ShowPrompt(
        string title,
        string message,
        string defaultValue = "",
        string okLabel = "OK",
        string cancelLabel = "Cancel")
    {
        ArgumentNullException.ThrowIfNull(okLabel);
        ArgumentNullException.ThrowIfNull(cancelLabel);
        _kind = DialogKind.Prompt;
        _title = title ?? string.Empty;
        _message = message ?? string.Empty;
        _input = defaultValue ?? string.Empty;
        _buttons.Clear();
        _buttons.Add(new DialogButton(okLabel, "ok"));
        _buttons.Add(new DialogButton(cancelLabel, "cancel"));
        _focusedButton = 0;
        Visible = true;
    }

    public void Dismiss()
    {
        Visible = false;
    }

    public void Tick()
    {
    }

    public bool HandleKey(ConsoleKeyInfo key)
    {
        if (!Visible)
        {
            return false;
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                Dismiss();
                return true;
            case ConsoleKey.Tab:
                CycleFocus(forward: true);
                return true;
            case ConsoleKey.LeftArrow:
                CycleFocus(forward: false);
                return true;
            case ConsoleKey.RightArrow:
                CycleFocus(forward: true);
                return true;
        }
        if (_kind == DialogKind.Prompt)
        {
            return HandlePromptKey(key);
        }
        return false;
    }

    private bool HandlePromptKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Enter)
        {
            return false;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (_input.Length > 0)
            {
                _input = _input[..^1];
            }
            return true;
        }
        char ch = key.KeyChar;
        if (!char.IsControl(ch))
        {
            _input += ch;
            return true;
        }
        return false;
    }

    private void CycleFocus(bool forward)
    {
        if (_buttons.Count == 0)
        {
            return;
        }
        if (forward)
        {
            _focusedButton = (_focusedButton + 1) % _buttons.Count;
        }
        else
        {
            _focusedButton = (_focusedButton - 1 + _buttons.Count) % _buttons.Count;
        }
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

        int width = Math.Min(MaxWidth, rect.Width - 2);
        int contentRows = CountMessageRows(width) + ButtonRowHeight + (Padding * 2) + (_kind == DialogKind.Prompt ? 1 : 0);
        int height = Math.Min(MaxHeight, Math.Max(MinHeight, Math.Min(rect.Height - 2, contentRows + 2)));
        int x = rect.X + (rect.Width - width) / 2;
        int y = rect.Y + (rect.Height - height) / 2;
        var box = new Rect(x, y, width, height);
        DrawBox(buffer, box);

        int textX = box.X + Padding;
        int textY = box.Y + Padding;
        int innerW = box.Width - (Padding * 2);
        DrawTitle(buffer, textX, textY, innerW);
        textY += 1;

        int messageRows = Math.Max(1, height - (Padding * 2) - 2 - ButtonRowHeight - (_kind == DialogKind.Prompt ? 1 : 0));
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
            buffer.SetText(textX, textY, "› ", ChatPalette.Accent);
            string input = _input.Length > innerW - 2 ? _input[(^Math.Max(1, innerW - 2))..] : _input;
            buffer.SetText(textX + 2, textY, input, ChatPalette.Accent);
            textY += 1;
        }

        DrawButtons(buffer, textX, box.Bottom - ButtonRowHeight - 1, innerW);
    }

    private void DrawTitle(ScreenBuffer buffer, int x, int y, int innerW)
    {
        string title = _title.Length > innerW ? _title[..Math.Max(0, innerW - 1)] + "…" : _title;
        buffer.SetText(x, y, title, ChatPalette.Accent);
    }

    private void DrawBox(ScreenBuffer buffer, Rect rect)
    {
        var fillStyle = new CellStyle(ChatPalette.Panel);
        var borderStyle = new CellStyle(ChatPalette.Border);
        buffer.Fill(rect, Cell.From(new Rune(' '), fillStyle));
        if (rect.Width < 2 || rect.Height < 2)
        {
            return;
        }
        int x1 = rect.X, y1 = rect.Y, x2 = rect.Right - 1, y2 = rect.Bottom - 1;
        buffer.At(x1, y1) = Cell.From(new Rune('╭'), borderStyle);
        buffer.At(x2, y1) = Cell.From(new Rune('╮'), borderStyle);
        buffer.At(x1, y2) = Cell.From(new Rune('╰'), borderStyle);
        buffer.At(x2, y2) = Cell.From(new Rune('╯'), borderStyle);
        for (int x = x1 + 1; x < x2; x++)
        {
            buffer.At(x, y1) = Cell.From(new Rune('─'), borderStyle);
            buffer.At(x, y2) = Cell.From(new Rune('─'), borderStyle);
        }
        for (int y = y1 + 1; y < y2; y++)
        {
            buffer.At(x1, y) = Cell.From(new Rune('│'), borderStyle);
            buffer.At(x2, y) = Cell.From(new Rune('│'), borderStyle);
        }
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
            var style = i == _focusedButton ? new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold) : ChatPalette.Muted;
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