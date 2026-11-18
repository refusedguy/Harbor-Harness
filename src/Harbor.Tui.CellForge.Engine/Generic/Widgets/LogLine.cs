namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Severity carried by a log entry. The viewer uses it only to pick a paint
/// style; no host service, event or chat type is involved.
/// </summary>
public enum LogLevel
{
    /// <summary>Finest tracing detail.</summary>
    Trace = 0,

    /// <summary>Diagnostic detail.</summary>
    Debug = 1,

    /// <summary>Normal operation (the default).</summary>
    Info = 2,

    /// <summary>Recoverable problem.</summary>
    Warn = 3,

    /// <summary>Failed operation.</summary>
    Error = 4,

    /// <summary>Terminal failure.</summary>
    Fatal = 5,
}

/// <summary>
/// One log entry: plain text plus a severity tag. Deliberately free of chat
/// vocabulary so a log source can feed the viewer without producing a chat
/// block. Null text is coerced to an empty string at construction.
/// </summary>
public readonly record struct LogLine
{
    /// <summary>Creates a line. Null <paramref name="text"/> becomes empty text.</summary>
    public LogLine(string? text, LogLevel level = LogLevel.Info)
    {
        Text = text ?? string.Empty;
        Level = level;
    }

    /// <summary>Line text, never null.</summary>
    public string Text { get; }

    /// <summary>Severity tag, used for the paint style.</summary>
    public LogLevel Level { get; }

    /// <summary>
    /// Rough resident size in bytes (UTF-16 characters plus fixed overhead).
    /// The scrollback ring uses this for byte-budget eviction, mirroring the
    /// chat timeline's byte accounting without naming a chat block.
    /// </summary>
    public int BudgetBytes => Text.Length * 2 + 32;
}
