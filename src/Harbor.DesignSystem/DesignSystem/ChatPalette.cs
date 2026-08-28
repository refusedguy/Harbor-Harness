using Harbor.DesignSystem;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Projection;

namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>
/// Shared cell styles for chat widgets — the terminal-side instance of the
/// HDS v1 design tokens (docs/design-system-report-20260827.html, §Design
/// Tokens). All raw colors are derived from
/// <see cref="TerminalColorPalette" /> in Harbor.DesignSystem — this file is
/// the PackedColor projection of the token catalog, not a second source of
/// truth. HDS §7.1 names ChatPalette as the single source of truth for block
/// colors inside the CellForge renderer: widget code must reference these
/// styles and never hardcode hex values.
///
/// Theme switching: the projection is a cached per-theme catalog, rebuilt
/// whenever <see cref="TerminalColorPalette.ThemeChanged" /> fires — reads are
/// a volatile field + member access on hot paths.
/// </summary>
public static class ChatPalette
{
    /// <summary>Per-theme projection: raw color tokens + derived cell styles.</summary>
    private sealed class Catalog
    {
        public Catalog(HarborTheme theme)
        {
            _accent = Project(theme.Accent);
            _success = Project(theme.Success);
            _warning = Project(theme.Warning);
            _error = Project(theme.Error);
            _tool = Project(theme.Tool);
            _systemToken = Project(theme.System);
            _text = Project(theme.Text);
            _muted = Project(theme.Muted);
            _bg = Project(theme.Background);
            _panel = Project(theme.Panel);
            _surface = Project(theme.Surface);
            _surface2 = Project(theme.Surface2);
            _border = Project(theme.Border);

            UserPrefixStyle = new CellStyle(_accent, attrs: StyleAttr.Bold);
            UserTextStyle = new CellStyle(attrs: StyleAttr.Bold);
            SystemStyle = new CellStyle(_muted, attrs: StyleAttr.Dim | StyleAttr.Italic);
            ToolNameStyle = new CellStyle(_tool, attrs: StyleAttr.Bold);
            ToolArgsStyle = new CellStyle(_text);
            ToolRunningStyle = new CellStyle(_warning);
            ToolOkStyle = new CellStyle(_success);
            ToolErrorStyle = new CellStyle(_error);
            ToolBodyStyle = new CellStyle(_text);
            DimStyle = new CellStyle(attrs: StyleAttr.Dim);
        }

        private static PackedColor Project(RgbColor c) => PackedColor.Rgb(c.R, c.G, c.B);

        public readonly PackedColor _accent, _success, _warning, _error, _tool, _systemToken, _text, _muted;
        public readonly PackedColor _bg, _panel, _surface, _surface2, _border;

        // Semantic cell styles (HDS v1 §1.3/§4.2 role map)
        public readonly CellStyle UserPrefixStyle, UserTextStyle, SystemStyle, ToolNameStyle, ToolArgsStyle;
        public readonly CellStyle ToolRunningStyle, ToolOkStyle, ToolErrorStyle, ToolBodyStyle, DimStyle;
    }

    private static readonly Catalog _initialCatalog = BuildInitial();

    private static volatile Catalog _catalog = _initialCatalog;

    private static Catalog BuildInitial()
    {
        TerminalColorPalette.ThemeChanged += (_, _) => _catalog = new Catalog(TerminalColorPalette.Current);
        return new Catalog(TerminalColorPalette.Current);
    }

    // ── HDS v1 raw color tokens (truecolor 24-bit) ─────────────────────────
    /// <summary>Accent — primary actions, active states (dark: #39BAE6).</summary>
    public static PackedColor Accent => _catalog._accent;
    /// <summary>Success — tool success, approved gates (dark: #7FD962).</summary>
    public static PackedColor Success => _catalog._success;
    /// <summary>Warning — pending approvals, running tools (dark: #FFB454).</summary>
    public static PackedColor Warning => _catalog._warning;
    /// <summary>Error — tool errors, denied gates (dark: #FF6B6B).</summary>
    public static PackedColor Error => _catalog._error;
    /// <summary>Tool — tool-call cards / role label (dark: #D2A6FF).</summary>
    public static PackedColor Tool => _catalog._tool;
    /// <summary>System — system notices role accent (dark: #F29668).</summary>
    public static PackedColor SystemToken => _catalog._systemToken;
    /// <summary>Primary text on the active surfaces.</summary>
    public static PackedColor Text => _catalog._text;
    /// <summary>Muted / secondary text and hints (dark: #5C6773).</summary>
    public static PackedColor Muted => _catalog._muted;

    // Surfaces — used by cell-diff rendering (borders, fills) when a block
    // needs an explicit background instead of the terminal default.
    /// <summary>BG — app background surface (dark: #0A0E14).</summary>
    public static PackedColor Bg => _catalog._bg;
    /// <summary>PANEL — elevated panel surface (dark: #0D1117).</summary>
    public static PackedColor Panel => _catalog._panel;
    /// <summary>SURFACE — card body surface (dark: #131820).</summary>
    public static PackedColor Surface => _catalog._surface;
    /// <summary>SURFACE2 — hover/active surface (dark: #1A1F2B).</summary>
    public static PackedColor Surface2 => _catalog._surface2;
    /// <summary>BORDER — hairline separators (dark: #1F2430).</summary>
    public static PackedColor Border => _catalog._border;

    // ── Semantic cell styles (HDS v1 §1.3/§4.2 role map) ───────────────────
    /// <summary>User prompt prefix — accent + bold.</summary>
    public static CellStyle UserPrefix => _catalog.UserPrefixStyle;

    /// <summary>User message body — bold default foreground.</summary>
    public static CellStyle UserText => _catalog.UserTextStyle;

    /// <summary>System notices — muted, dim italic.</summary>
    public static CellStyle System => _catalog.SystemStyle;

    /// <summary>Tool header name — tool token + bold.</summary>
    public static CellStyle ToolName => _catalog.ToolNameStyle;

    /// <summary>Tool argument preview — primary text tone.</summary>
    public static CellStyle ToolArgs => _catalog.ToolArgsStyle;

    /// <summary>Running tool state — warning.</summary>
    public static CellStyle ToolRunning => _catalog.ToolRunningStyle;

    /// <summary>Successful tool state — success green.</summary>
    public static CellStyle ToolOk => _catalog.ToolOkStyle;

    /// <summary>Failed tool state — error red.</summary>
    public static CellStyle ToolError => _catalog.ToolErrorStyle;

    /// <summary>Tool output body — primary text tone.</summary>
    public static CellStyle ToolBody => _catalog.ToolBodyStyle;

    /// <summary>Gutters, hints, secondary metadata — SGR dim (terminal-level
    /// muted rendering; keeps goldens stable across truecolor capability).</summary>
    public static CellStyle Dim => _catalog.DimStyle;
}
