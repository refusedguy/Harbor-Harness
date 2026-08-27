using Harbor.DesignSystem;
using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Ui.Framework.Projection;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>
/// Shared cell styles for chat widgets — the terminal-side instance of the
/// HDS v1 design tokens (docs/design-system-report-20260827.html, §Design
/// Tokens). All raw colors are derived from
/// <see cref="TerminalColorPalette" /> in Harbor.DesignSystem — this file is
/// the PackedColor projection of the token catalog, not a second source of
/// truth. HDS §7.1 names ChatPalette as the single source of truth for block
/// colors inside the ConsoleEx renderer: widget code must reference these
/// styles and never hardcode hex values.
/// </summary>
public static class ChatPalette
{
    // ── Token bridge (Harbor.DesignSystem → truecolor cells) ────────────────
    private static PackedColor FromToken(RgbColor c) => PackedColor.Rgb(c.R, c.G, c.B);

    // ── HDS v1 raw color tokens (truecolor 24-bit) ─────────────────────────
    /// <summary>Accent — primary actions, active states (#39BAE6).</summary>
    public static readonly PackedColor Accent = FromToken(TerminalColorPalette.Accent);
    /// <summary>Success — tool success, approved gates (#7FD962).</summary>
    public static readonly PackedColor Success = FromToken(TerminalColorPalette.Success);
    /// <summary>Warning — pending approvals, running tools (#FFB454).</summary>
    public static readonly PackedColor Warning = FromToken(TerminalColorPalette.Warning);
    /// <summary>Error — tool errors, denied gates (#FF6B6B).</summary>
    public static readonly PackedColor Error = FromToken(TerminalColorPalette.Error);
    /// <summary>Tool — tool-call cards / role label (#D2A6FF).</summary>
    public static readonly PackedColor Tool = FromToken(TerminalColorPalette.Tool);
    /// <summary>System — system notices role accent (#F29668).</summary>
    public static readonly PackedColor SystemToken = FromToken(TerminalColorPalette.System);
    /// <summary>Primary text on dark surfaces.</summary>
    public static readonly PackedColor Text = FromToken(TerminalColorPalette.Text);
    /// <summary>Muted / secondary text and hints (#5C6773).</summary>
    public static readonly PackedColor Muted = FromToken(TerminalColorPalette.Muted);

    // Surfaces — used by cell-diff rendering (borders, fills) when a block
    // needs an explicit background instead of the terminal default.
    /// <summary>BG — app background surface (#0A0E14).</summary>
    public static readonly PackedColor Bg = FromToken(TerminalColorPalette.Background);
    /// <summary>PANEL — elevated panel surface (#0D1117).</summary>
    public static readonly PackedColor Panel = FromToken(TerminalColorPalette.Panel);
    /// <summary>SURFACE — card body surface (#131820).</summary>
    public static readonly PackedColor Surface = FromToken(TerminalColorPalette.Surface);
    /// <summary>SURFACE2 — hover/active surface (#1A1F2B).</summary>
    public static readonly PackedColor Surface2 = FromToken(TerminalColorPalette.Surface2);
    /// <summary>BORDER — hairline separators (#1F2430).</summary>
    public static readonly PackedColor Border = FromToken(TerminalColorPalette.Border);

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
