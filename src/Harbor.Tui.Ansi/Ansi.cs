namespace Harbor.Tui.Ansi;

/// <summary>
/// ANSI escape code helper. Pure static, AOT-compatible.
/// </summary>
public static class Ansi
{
    // Reset / decoration
    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[2m";
    public const string Italic = "\x1b[3m";
    public const string Underline = "\x1b[4m";
    public const string Strike = "\x1b[9m";

    // Foreground (16-color)
    public const string Black = "\x1b[30m";
    public const string Red = "\x1b[31m";
    public const string Green = "\x1b[32m";
    public const string Yellow = "\x1b[33m";
    public const string Blue = "\x1b[34m";
    public const string Magenta = "\x1b[35m";
    public const string Cyan = "\x1b[36m";
    public const string White = "\x1b[37m";
    public const string BrightBlack = "\x1b[90m";
    public const string BrightRed = "\x1b[91m";
    public const string BrightGreen = "\x1b[92m";
    public const string BrightYellow = "\x1b[93m";
    public const string BrightBlue = "\x1b[94m";
    public const string BrightMagenta = "\x1b[95m";
    public const string BrightCyan = "\x1b[96m";
    public const string BrightWhite = "\x1b[97m";

    // 256-color
    public static string Fg(int n) => $"\x1b[38;5;{n}m";
    public static string Bg(int n) => $"\x1b[48;5;{n}m";

    // TrueColor
    public static string Fg(int r, int g, int b) => $"\x1b[38;2;{r};{g};{b}m";
    public static string Bg(int r, int g, int b) => $"\x1b[48;2;{r};{g};{b}m";

    // Cursor
    public static void MoveTo(int row, int col) => Console.Write($"\x1b[{row};{col}H");
    public static void MoveUp(int n) => Console.Write($"\x1b[{n}A");
    public static void MoveDown(int n) => Console.Write($"\x1b[{n}B");
    public static void MoveRight(int n) => Console.Write($"\x1b[{n}C");
    public static void MoveLeft(int n) => Console.Write($"\x1b[{n}D");
    public static void ClearLine() => Console.Write("\x1b[2K\r");
    public static void ClearLineFromCursor() => Console.Write("\x1b[K");
    public static void ClearScreen() => Console.Write("\x1b[2J\x1b[H");
    public static void HideCursor() => Console.Write("\x1b[?25l");
    public static void ShowCursor() => Console.Write("\x1b[?25h");

    // Screen buffer
    public static void EnterAltScreen() => Console.Write("\x1b[?1049h");
    public static void ExitAltScreen() => Console.Write("\x1b[?1049l");

    public static void WriteColored(string text, string fg, string bg = "")
    {
        if (!string.IsNullOrEmpty(fg)) Console.Write(fg);
        if (!string.IsNullOrEmpty(bg)) Console.Write(bg);
        Console.Write(text);
        Console.Write(Reset);
    }

    public static void WriteLineColored(string text, string fg, string bg = "")
    {
        WriteColored(text, fg, bg);
        Console.WriteLine();
    }
}
