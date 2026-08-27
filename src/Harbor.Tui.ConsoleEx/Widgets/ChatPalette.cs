namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>
/// Shared cell styles for chat widgets — the terminal-side instance of the
/// HDS v1 design tokens (docs/design-system-report-20260827.html, §Design
/// Tokens). HDS §7.1 names ChatPalette as the single source of truth for
/// block colors in the ConsoleEx renderer; widget code must reference these
/// styles (or the raw <see cref="PackedColor" /> tokens below) and never
/// hardcode hex values.
/// </summary>
public static class ChatPalette
{
    // ── HDS v1 raw color tokens (truecolor 24-bit) ─────────────────────────
    /// <summary>Accent — primary actions, active states (#39BAE6).</summary>
    public static readonly PackedColor Accent = PackedColor.Rgb(0x39, 0xBA, 0xE6);
    /// <summary>Success — tool success, approved gates (#7FD962).</summary>
    public static readonly PackedColor Success = PackedColor.Rgb(0x7F, 0xD9, 0x62);
    /// <summary>Warning — pending approvals, running tools (#FFB454).</summary>
    public static readonly PackedColor Warning = PackedColor.Rgb(0xFF, 0xB4, 0x54);
    /// <summary>Error — tool errors, denied gates (#FF6B6B).</summary>
    public static readonly PackedColor Error = PackedColor.Rgb(0xFF, 0x6B, 0x6B);
    /// <summary>Tool — tool-call cards / role label (#D2A6FF).</summary>
    public static readonly PackedColor Tool = PackedColor.Rgb(0xD2, 0xA6, 0xFF);
    /// <summary>System — system notices role accent (#F29668).</summary>
    public static readonly PackedColor SystemToken = PackedColor.Rgb(0xF2, 0x96, 0x68);
    /// <summary>Primary text on dark surfaces (#B3B9C5).</summary>
    public static readonly PackedColor Text = PackedColor.Rgb(0xB3, 0xB9, 0xC5);
    /// <summary>Muted / secondary text and hints (#5C6773).</summary>
    public static readonly PackedColor Muted = PackedColor.Rgb(0x5C, 0x67, 0x73);

    // Surfaces — used by cell-diff rendering (borders, fills) when a block
    // needs an explicit background instead of the terminal default.
    /// <summary>BG — app background surface (#0A0E14).</summary>
    public static readonly PackedColor Bg = PackedColor.Rgb(0x0A, 0x0E, 0x14);
    /// <summary>PANEL — elevated panel surface (#0D1117).</summary>
    public static readonly PackedColor Panel = PackedColor.Rgb(0x0D, 0x11, 0x17);
    /// <summary>SURFACE — card body surface (#131820).</summary>
    public static readonly PackedColor Surface = PackedColor.Rgb(0x13, 0x18, 0x20);
    /// <summary>SURFACE2 — hover/active surface (#1A1F2B).</summary>
    public static readonly PackedColor Surface2 = PackedColor.Rgb(0x1A, 0x1F, 0x2B);
    /// <summary>BORDER — hairline separators (#1F2430).</summary>
    public static readonly PackedColor Border = PackedColor.Rgb(0x1F, 0x24, 0x30);

    // ── Semantic cell styles (HDS v1 §1.3/§4.2 role map) ───────────────────
    /// <summary>User prompt prefix — accent + bold.</summary>
    public static readonly CellStyle UserPrefix = new(Accent, attrs: StyleAttr.Bold);

    /// <summary>User message body — bold default foreground.</summary>
    public static readonly CellStyle UserText = new(attrs: StyleAttr.Bold);

    /// <summary>System notices — muted, dim italic.</summary>
    public static readonly CellStyle System = new(Muted, attrs: StyleAttr.Dim | StyleAttr.Italic);

    /// <summary>Tool header name — tool token + bold.</summary>
    public static readonly CellStyle ToolName = new(Tool, attrs: StyleAttr.Bold);

    /// <summary>Tool argument preview — primary text tone.</summary>
    public static readonly CellStyle ToolArgs = new(Text);

    /// <summary>Running tool state — warning.</summary>
    public static readonly CellStyle ToolRunning = new(Warning);

    /// <summary>Successful tool state — success green.</summary>
    public static readonly CellStyle ToolOk = new(Success);

    /// <summary>Failed tool state — error red.</summary>
    public static readonly CellStyle ToolError = new(Error);

    /// <summary>Tool output body — primary text tone.</summary>
    public static readonly CellStyle ToolBody = new(Text);

    /// <summary>Gutters, hints, secondary metadata — SGR dim (terminal-level
    /// muted rendering; keeps goldens stable across truecolor capability).</summary>
    public static readonly CellStyle Dim = new(attrs: StyleAttr.Dim);
}
