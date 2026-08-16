using System.Net;
using System.Text;

namespace Harbor.E2E.Framework;

/// <summary>
///     Represents a single cell in the terminal grid.
/// </summary>
internal struct TerminalCell
{
    public char Character;
    public string FgColor;
    public string BgColor;
    public bool Bold;
    public bool Underline;

    public TerminalCell(char character, string fgColor, string bgColor, bool bold = false, bool underline = false)
    {
        Character = character;
        FgColor = fgColor;
        BgColor = bgColor;
        Bold = bold;
        Underline = underline;
    }
}

/// <summary>
///     Emulates a 2D Terminal Screen Buffer (e.g. 120x50 cells) by parsing incoming
///     raw stdout ANSI streams (cursor movements, SGR colors, line/screen clears).
///     Enables pixel-perfect HTML rendering of TUI screens for E2E screenshots.
/// </summary>
internal sealed class AnsiTerminalBuffer
{
    private const string DefaultFg = "#e6e6e6";
    private const string DefaultBg = "#0d0d0f";

    private static readonly string[] StandardColors =
    [
        "#000000", "#cd3131", "#0dbc79", "#e5e510",
        "#2472c8", "#bc3fbc", "#11a8cd", "#e5e5e5"
    ];

    private static readonly string[] BrightColors =
    [
        "#666666", "#f14c4c", "#23d18b", "#f5f543",
        "#3b8eea", "#d670d6", "#29b8db", "#ffffff"
    ];

    private readonly int _width;
    private readonly int _height;
    private readonly TerminalCell[,] _grid;

    private int _cursorRow;
    private int _cursorCol;

    private string _currentFg = DefaultFg;
    private string _currentBg = DefaultBg;
    private bool _currentBold;
    private bool _currentUnderline;

    public AnsiTerminalBuffer(int width = 120, int height = 50)
    {
        _width = width;
        _height = height;
        _grid = new TerminalCell[_height, _width];
        ClearEntireScreen();
    }

    public int Width => _width;
    public int Height => _height;
    public int CursorRow => _cursorRow;
    public int CursorCol => _cursorCol;

    public void Write(string input)
    {
        if (string.IsNullOrEmpty(input))
            return;

        int i = 0;
        int len = input.Length;

        while (i < len)
        {
            char c = input[i];

            if (c == '\u001b' && i + 1 < len)
            {
                char next = input[i + 1];
                if (next == '[')
                {
                    // CSI sequence: ESC [ <params> <final>
                    int j = i + 2;
                    var paramSb = new StringBuilder();
                    while (j < len)
                    {
                        char p = input[j];
                        j++;
                        // Final byte of CSI sequence is in range 0x40..0x7E (@..~)
                        if (p >= 0x40 && p <= 0x7E)
                        {
                            ParseCsiSequence(paramSb.ToString(), p);
                            break;
                        }

                        paramSb.Append(p);
                    }

                    i = j;
                    continue;
                }

                if (next == ']')
                {
                    // OSC sequence: ESC ] ... BEL or ST
                    int j = i + 2;
                    while (j < len)
                    {
                        char p = input[j];
                        j++;
                        if (p == '\u0007') break; // BEL
                        if (p == '\u001b' && j < len && input[j] == '\\')
                        {
                            j++;
                            break;
                        }
                    }

                    i = j;
                    continue;
                }

                // Bare escape (e.g. ESC 7, ESC 8, ESC =)
                i += 2;
                continue;
            }

            if (c == '\r')
            {
                _cursorCol = 0;
                i++;
                continue;
            }

            if (c == '\n')
            {
                AdvanceLine();
                i++;
                continue;
            }

            if (c == '\b')
            {
                _cursorCol = Math.Max(0, _cursorCol - 1);
                i++;
                continue;
            }

            if (c == '\t')
            {
                int nextTab = (_cursorCol + 8) & ~7;
                _cursorCol = Math.Min(_width - 1, nextTab);
                i++;
                continue;
            }

            if (c == '\0')
            {
                i++;
                continue;
            }

            // Printable character
            PutChar(c);
            i++;
        }
    }

    private void PutChar(char c)
    {
        if (_cursorRow >= _height)
        {
            ScrollUp(1);
            _cursorRow = _height - 1;
        }

        if (_cursorCol >= _width)
        {
            _cursorCol = 0;
            _cursorRow++;
            if (_cursorRow >= _height)
            {
                ScrollUp(1);
                _cursorRow = _height - 1;
            }
        }

        _grid[_cursorRow, _cursorCol] = new TerminalCell(c, _currentFg, _currentBg, _currentBold, _currentUnderline);
        _cursorCol++;
    }

    private void AdvanceLine()
    {
        _cursorCol = 0;
        _cursorRow++;
        if (_cursorRow >= _height)
        {
            ScrollUp(1);
            _cursorRow = _height - 1;
        }
    }

    private void ScrollUp(int lines)
    {
        for (int r = 0; r < _height - lines; r++)
        {
            for (int c = 0; c < _width; c++)
            {
                _grid[r, c] = _grid[r + lines, c];
            }
        }

        for (int r = _height - lines; r < _height; r++)
        {
            for (int c = 0; c < _width; c++)
            {
                _grid[r, c] = new TerminalCell(' ', DefaultFg, DefaultBg);
            }
        }
    }

    private void ClearEntireScreen()
    {
        for (int r = 0; r < _height; r++)
        {
            for (int c = 0; c < _width; c++)
            {
                _grid[r, c] = new TerminalCell(' ', DefaultFg, DefaultBg);
            }
        }
    }

    private void ParseCsiSequence(string paramStr, char command)
    {
        // Handle private-mode sequences (ESC [ ? ...) — strip the '?' prefix
        // so params like "?1049", "?25", "?2J" are parsed correctly.
        bool isPrivate = paramStr.StartsWith('?');
        if (isPrivate)
            paramStr = paramStr[1..];

        string[] parts = paramStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
        int[] args = new int[parts.Length];
        for (int k = 0; k < parts.Length; k++)
        {
            _ = int.TryParse(parts[k], out args[k]);
        }

        int p0 = args.Length > 0 ? args[0] : 0;
        int p1 = args.Length > 1 ? args[1] : 0;

        // Handle private-mode sequences first.
        if (isPrivate)
        {
            switch (command)
            {
                case 'h': // Set private mode
                    if (p0 == 1049 || p0 == 47 || p0 == 1047)
                    {
                        ClearEntireScreen();
                        _cursorRow = 0;
                        _cursorCol = 0;
                    }
                    return;
                case 'l': // Reset private mode
                    if (p0 == 1049 || p0 == 47 || p0 == 1047)
                    {
                        ClearEntireScreen();
                        _cursorRow = 0;
                        _cursorCol = 0;
                    }
                    return;
                case 'J': // Erase in Display (private mode) — treat same as non-private
                    if (p0 == 0 || p0 == 2)
                    {
                        ClearEntireScreen();
                        _cursorRow = 0;
                        _cursorCol = 0;
                    }
                    return;
            }
        }

        switch (command)
        {
            case 'H':
            case 'f':
            {
                int r = (p0 > 0 ? p0 : 1) - 1;
                int c = (p1 > 0 ? p1 : 1) - 1;
                _cursorRow = Math.Clamp(r, 0, _height - 1);
                _cursorCol = Math.Clamp(c, 0, _width - 1);
                break;
            }
            case 'A': // Cursor Up
            {
                int count = p0 > 0 ? p0 : 1;
                _cursorRow = Math.Max(0, _cursorRow - count);
                break;
            }
            case 'B': // Cursor Down
            {
                int count = p0 > 0 ? p0 : 1;
                _cursorRow = Math.Min(_height - 1, _cursorRow + count);
                break;
            }
            case 'C': // Cursor Forward
            {
                int count = p0 > 0 ? p0 : 1;
                _cursorCol = Math.Min(_width - 1, _cursorCol + count);
                break;
            }
            case 'D': // Cursor Backward
            {
                int count = p0 > 0 ? p0 : 1;
                _cursorCol = Math.Max(0, _cursorCol - count);
                break;
            }
            case 'J': // Erase in Display
            {
                if (p0 == 2 || p0 == 3)
                {
                    ClearEntireScreen();
                    _cursorRow = 0;
                    _cursorCol = 0;
                }
                else if (p0 == 0)
                {
                    // Clear from cursor to end of screen
                    for (int c = _cursorCol; c < _width; c++)
                        _grid[_cursorRow, c] = new TerminalCell(' ', _currentFg, _currentBg);
                    for (int r = _cursorRow + 1; r < _height; r++)
                        for (int c = 0; c < _width; c++)
                            _grid[r, c] = new TerminalCell(' ', _currentFg, _currentBg);
                }
                else if (p0 == 1)
                {
                    // Clear from start of screen to cursor
                    for (int r = 0; r < _cursorRow; r++)
                        for (int c = 0; c < _width; c++)
                            _grid[r, c] = new TerminalCell(' ', _currentFg, _currentBg);
                    for (int c = 0; c <= _cursorCol; c++)
                        _grid[_cursorRow, c] = new TerminalCell(' ', _currentFg, _currentBg);
                }

                break;
            }
            case 'K': // Erase in Line
            {
                if (p0 == 0)
                {
                    for (int c = _cursorCol; c < _width; c++)
                        _grid[_cursorRow, c] = new TerminalCell(' ', _currentFg, _currentBg);
                }
                else if (p0 == 1)
                {
                    for (int c = 0; c <= _cursorCol; c++)
                        _grid[_cursorRow, c] = new TerminalCell(' ', _currentFg, _currentBg);
                }
                else if (p0 == 2)
                {
                    for (int c = 0; c < _width; c++)
                        _grid[_cursorRow, c] = new TerminalCell(' ', _currentFg, _currentBg);
                }

                break;
            }
            case 'm': // Select Graphic Rendition (SGR)
            {
                if (args.Length == 0)
                {
                    ResetSgr();
                    break;
                }

                int idx = 0;
                while (idx < args.Length)
                {
                    int code = args[idx];
                    if (code == 0)
                    {
                        ResetSgr();
                        idx++;
                    }
                    else if (code == 1)
                    {
                        _currentBold = true;
                        idx++;
                    }
                    else if (code == 4)
                    {
                        _currentUnderline = true;
                        idx++;
                    }
                    else if (code == 22)
                    {
                        _currentBold = false;
                        idx++;
                    }
                    else if (code == 24)
                    {
                        _currentUnderline = false;
                        idx++;
                    }
                    else if (code >= 30 && code <= 37)
                    {
                        _currentFg = StandardColors[code - 30];
                        idx++;
                    }
                    else if (code == 39)
                    {
                        _currentFg = DefaultFg;
                        idx++;
                    }
                    else if (code >= 40 && code <= 47)
                    {
                        _currentBg = StandardColors[code - 40];
                        idx++;
                    }
                    else if (code == 49)
                    {
                        _currentBg = DefaultBg;
                        idx++;
                    }
                    else if (code >= 90 && code <= 97)
                    {
                        _currentFg = BrightColors[code - 90];
                        idx++;
                    }
                    else if (code >= 100 && code <= 107)
                    {
                        _currentBg = BrightColors[code - 100];
                        idx++;
                    }
                    else if (code == 38 && idx + 4 < args.Length && args[idx + 1] == 2)
                    {
                        // 24-bit TrueColor FG: 38;2;r;g;b
                        int r = args[idx + 2];
                        int g = args[idx + 3];
                        int b = args[idx + 4];
                        _currentFg = $"#{r:X2}{g:X2}{b:X2}";
                        idx += 5;
                    }
                    else if (code == 48 && idx + 4 < args.Length && args[idx + 1] == 2)
                    {
                        // 24-bit TrueColor BG: 48;2;r;g;b
                        int r = args[idx + 2];
                        int g = args[idx + 3];
                        int b = args[idx + 4];
                        _currentBg = $"#{r:X2}{g:X2}{b:X2}";
                        idx += 5;
                    }
                    else if (code == 38 && idx + 2 < args.Length && args[idx + 1] == 5)
                    {
                        // 256 color FG: 38;5;n
                        int n = args[idx + 2];
                        _currentFg = Map256Color(n);
                        idx += 3;
                    }
                    else if (code == 48 && idx + 2 < args.Length && args[idx + 1] == 5)
                    {
                        // 256 color BG: 48;5;n
                        int n = args[idx + 2];
                        _currentBg = Map256Color(n);
                        idx += 3;
                    }
                    else
                    {
                        idx++;
                    }
                }

                break;
            }
        }
    }

    private void ResetSgr()
    {
        _currentFg = DefaultFg;
        _currentBg = DefaultBg;
        _currentBold = false;
        _currentUnderline = false;
    }

    private static string Map256Color(int index)
    {
        if (index is >= 0 and <= 7) return StandardColors[index];
        if (index is >= 8 and <= 15) return BrightColors[index - 8];
        if (index is >= 16 and <= 231)
        {
            int n = index - 16;
            int r = (n / 36) * 51;
            int g = ((n / 6) % 6) * 51;
            int b = (n % 6) * 51;
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        if (index is >= 232 and <= 255)
        {
            int gray = (index - 232) * 10 + 8;
            return $"#{gray:X2}{gray:X2}{gray:X2}";
        }

        return DefaultFg;
    }

    /// <summary>
    ///     Convert the 2D grid into an HTML document with colored spans and CSS layout,
    ///     ready for rendering via Chromium headless into a pixel-perfect PNG.
    /// </summary>
    public string ToHtml()
    {
        // Find last non-empty row to trim trailing whitespace
        int lastRow = _height - 1;
        while (lastRow > 0 && IsRowEmpty(lastRow))
        {
            lastRow--;
        }

        var sb = new StringBuilder();
        sb.Append("""
                  <!DOCTYPE html>
                  <html lang="en">
                  <head>
                    <meta charset="utf-8" />
                    <style>
                      html, body {
                        margin: 0;
                        padding: 12px;
                        background: #0d0d0f;
                        color: #e6e6e6;
                        font-family: "DejaVu Sans Mono", "Liberation Mono", "Courier New", monospace;
                        font-size: 13px;
                        line-height: 1.2;
                        white-space: pre;
                      }
                      .term-row {
                        height: 1.2em;
                        overflow: hidden;
                      }
                    </style>
                  </head>
                  <body>
                  """);

        for (int r = 0; r <= lastRow; r++)
        {
            sb.Append("<div class=\"term-row\">");
            int c = 0;
            while (c < _width)
            {
                var cell = _grid[r, c];
                string fg = cell.FgColor;
                string bg = cell.BgColor;
                bool bold = cell.Bold;
                bool underline = cell.Underline;

                var runSb = new StringBuilder();
                while (c < _width &&
                       _grid[r, c].FgColor == fg &&
                       _grid[r, c].BgColor == bg &&
                       _grid[r, c].Bold == bold &&
                       _grid[r, c].Underline == underline)
                {
                    runSb.Append(_grid[r, c].Character == '\0' ? ' ' : _grid[r, c].Character);
                    c++;
                }

                string chunk = WebUtility.HtmlEncode(runSb.ToString());
                bool isDefaultStyle = (fg == DefaultFg || string.Equals(fg, "#e6e6e6", StringComparison.OrdinalIgnoreCase)) &&
                                     (bg == DefaultBg || string.Equals(bg, "#0d0d0f", StringComparison.OrdinalIgnoreCase)) &&
                                     !bold && !underline;

                if (isDefaultStyle)
                {
                    sb.Append(chunk);
                }
                else
                {
                    sb.Append("<span style=\"");
                    if (fg != DefaultFg) sb.Append($"color:{fg};");
                    if (bg != DefaultBg) sb.Append($"background-color:{bg};");
                    if (bold) sb.Append("font-weight:bold;");
                    if (underline) sb.Append("text-decoration:underline;");
                    sb.Append("\">");
                    sb.Append(chunk);
                    sb.Append("</span>");
                }
            }

            sb.AppendLine("</div>");
        }

        sb.Append("""
                  </body>
                  </html>
                  """);

        return sb.ToString();
    }

    private bool IsRowEmpty(int row)
    {
        for (int c = 0; c < _width; c++)
        {
            char ch = _grid[row, c].Character;
            if (ch != ' ' && ch != '\0')
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Returns the entire visible grid as a single string with rows
    ///     separated by newlines. Trailing whitespace on each row is trimmed.
    /// </summary>
    public string GetVisibleText()
    {
        var sb = new StringBuilder(_height * (_width + 1));
        for (int r = 0; r < _height; r++)
        {
            var rowSb = new StringBuilder(_width);
            for (int c = 0; c < _width; c++)
            {
                char ch = _grid[r, c].Character;
                rowSb.Append(ch == '\0' ? ' ' : ch);
            }
            sb.AppendLine(rowSb.ToString().TrimEnd());
        }
        return sb.ToString();
    }

    /// <summary>
    ///     Search the visible 2D grid for <paramref name="pattern" /> as a
    ///     case-sensitive substring. Rows are searched left-to-right, top-to-bottom.
    ///     This correctly handles renderers that use cursor positioning to
    ///     overwrite text (e.g. Spectre.Console re-rendering the same screen
    ///     region) — the grid reflects what is actually visible, not the raw
    ///     append-only byte stream.
    /// </summary>
    public bool ContainsText(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;

        for (int r = 0; r < _height; r++)
        {
            var rowSb = new StringBuilder(_width);
            for (int c = 0; c < _width; c++)
            {
                char ch = _grid[r, c].Character;
                rowSb.Append(ch == '\0' ? ' ' : ch);
            }
            if (rowSb.ToString().Contains(pattern, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
