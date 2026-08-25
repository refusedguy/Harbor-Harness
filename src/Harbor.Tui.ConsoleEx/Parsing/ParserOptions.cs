namespace Harbor.Tui.ConsoleEx.Parsing;

/// <summary>Parser budgets and limits (§5.3 of the ConsoleEx design).</summary>
public sealed class ParserOptions
{
    /// <summary>Maximum raw parameter bytes inside one CSI sequence.</summary>
    public const int DefaultMaxParamsBytes = 16;

    /// <summary>Maximum intermediate bytes (0x20–0x2F) inside one CSI sequence.</summary>
    public const int DefaultMaxIntermediatesBytes = 2;

    /// <summary>Paste-flood guard: payloads above this size are truncated.</summary>
    public const int DefaultMaxPasteBytes = 256 * 1024;

    /// <summary>OSC/DCS-string consumption guard against unterminated garbage.</summary>
    public const int DefaultMaxStringBytes = 4096;

    public int MaxParamsBytes { get; init; } = DefaultMaxParamsBytes;
    public int MaxIntermediatesBytes { get; init; } = DefaultMaxIntermediatesBytes;
    public int MaxPasteBytes { get; init; } = DefaultMaxPasteBytes;
    public int MaxStringBytes { get; init; } = DefaultMaxStringBytes;

    public static ParserOptions Default { get; } = new();
}
