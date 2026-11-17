namespace Harbor.Tui.CellForge.Parsing;

/// <summary>Parser budgets and limits (§5.3 of the CellForge design).</summary>
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

    /// <summary>
    /// Optional sink for malformed/ignored input sequences (#1110). The parser
    /// always counts drops; without a sink those counts stay internal and the
    /// drops are silent. A host that wants diagnostics sets this to log or
    /// surface the snapshot instead of losing the signal.
    /// </summary>
    /// <remarks>
    /// The sink is null by default. The drop path pays one null check and
    /// keeps its existing counter increment, with no delegate call or allocation.
    /// The callback runs on the reader thread inside the parser state machine.
    /// </remarks>
    public Action<ParserDrop>? OnDrop { get; init; }

    public static ParserOptions Default { get; } = new();
}

/// <summary>
/// One parser drop notification: the running malformed/ignored counts after
/// the increment that triggered it (#1110).
/// </summary>
public readonly record struct ParserDrop(int MalformedCount, int IgnoredCount);
