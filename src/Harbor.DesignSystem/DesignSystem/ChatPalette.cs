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
            ToolPillRunningStyle = new CellStyle(_warning);
            ToolPillOkStyle = new CellStyle(_success);
            ToolPillErrorStyle = new CellStyle(_error);
            DimStyle = new CellStyle(attrs: StyleAttr.Dim);
        }

        private static PackedColor Project(RgbColor c) => PackedColor.Rgb(c.R, c.G, c.B);

        public readonly PackedColor _accent, _success, _warning, _error, _tool, _systemToken, _text, _muted;
        public readonly PackedColor _bg, _panel, _surface, _surface2, _border;

        // Semantic cell styles (HDS v1 §1.3/§4.2 role map)
        public readonly CellStyle UserPrefixStyle, UserTextStyle, SystemStyle, ToolNameStyle, ToolArgsStyle;
        public readonly CellStyle ToolRunningStyle, ToolOkStyle, ToolErrorStyle, ToolBodyStyle;
        public readonly CellStyle ToolPillRunningStyle, ToolPillOkStyle, ToolPillErrorStyle, DimStyle;
    }

    private static readonly Catalog _initialCatalog = BuildInitial();

    private static volatile Catalog _catalog = _initialCatalog;

    // Frame-boundary snapshot (renderer-moat T2 hot-swap): the render thread
    // pins the catalog for one frame, so a theme published mid-paint cannot
    // tear it — cells painted before and after the publish resolve against
    // one coherent projection. Per-thread: N concurrent render loops (CellForge,
    // Avalonia, Blazor mirrors) pin independently; unpinned readers always
    // see the live catalog.
    [ThreadStatic]
    private static Catalog? _framePinned;

    private static Catalog BuildInitial()
    {
        TerminalColorPalette.ThemeChanged += (_, _) => _catalog = new Catalog(TerminalColorPalette.Current);
        return new Catalog(TerminalColorPalette.Current);
    }

    /// <summary>
    /// Frame-boundary snapshot (renderer-moat hot-swap): pins the current
    /// catalog for the CALLING render thread until <see cref="UnpinFrame"/>.
    /// A concurrent <see cref="TerminalColorPalette.Apply"/> publishes a new
    /// catalog but cannot tear the pinned frame; the new catalog is picked up
    /// by the next pin. Lock-free — a volatile field read plus a ThreadStatic
    /// assignment; callers on other threads are unaffected.
    /// </summary>
    public static void PinFrame() => _framePinned = _catalog;

    /// <summary>Ends the pinned-frame scope for the calling thread.</summary>
    public static void UnpinFrame() => _framePinned = null;

    /// <summary>Active projection for the calling thread: the pinned frame
    /// snapshot when one is armed, the live catalog otherwise.</summary>
    private static Catalog C => _framePinned ?? _catalog;

    // ── HDS v1 raw color tokens (truecolor 24-bit) ─────────────────────────
    /// <summary>Accent — primary actions, active states (dark: #39BAE6).</summary>
    public static PackedColor Accent => C._accent;
    /// <summary>Success — tool success, approved gates (dark: #7FD962).</summary>
    public static PackedColor Success => C._success;
    /// <summary>Warning — pending approvals, running tools (dark: #FFB454).</summary>
    public static PackedColor Warning => C._warning;
    /// <summary>Error — tool errors, denied gates (dark: #FF6B6B).</summary>
    public static PackedColor Error => C._error;
    /// <summary>Tool — tool-call cards / role label (dark: #D2A6FF).</summary>
    public static PackedColor Tool => C._tool;
    /// <summary>System — system notices role accent (dark: #F29668).</summary>
    public static PackedColor SystemToken => C._systemToken;
    /// <summary>Primary text on the active surfaces.</summary>
    public static PackedColor Text => C._text;
    /// <summary>Muted / secondary text and hints (dark: #5C6773).</summary>
    public static PackedColor Muted => C._muted;

    // Surfaces — used by cell-diff rendering (borders, fills) when a block
    // needs an explicit background instead of the terminal default.
    /// <summary>BG — app background surface (dark: #0A0E14).</summary>
    public static PackedColor Bg => C._bg;
    /// <summary>PANEL — elevated panel surface (dark: #0D1117).</summary>
    public static PackedColor Panel => C._panel;
    /// <summary>SURFACE — card body surface (dark: #131820).</summary>
    public static PackedColor Surface => C._surface;
    /// <summary>SURFACE2 — hover/active surface (dark: #1A1F2B).</summary>
    public static PackedColor Surface2 => C._surface2;
    /// <summary>BORDER — hairline separators (dark: #1F2430).</summary>
    public static PackedColor Border => C._border;

    // ── Semantic cell styles (HDS v1 §1.3/§4.2 role map) ───────────────────
    /// <summary>User prompt prefix — accent + bold.</summary>
    public static CellStyle UserPrefix => C.UserPrefixStyle;

    /// <summary>User message body — bold default foreground.</summary>
    public static CellStyle UserText => C.UserTextStyle;

    /// <summary>System notices — muted, dim italic.</summary>
    public static CellStyle System => C.SystemStyle;

    /// <summary>Tool header name — tool token + bold.</summary>
    public static CellStyle ToolName => C.ToolNameStyle;

    /// <summary>Tool argument preview — primary text tone.</summary>
    public static CellStyle ToolArgs => C.ToolArgsStyle;

    /// <summary>Running tool state — warning.</summary>
    public static CellStyle ToolRunning => C.ToolRunningStyle;

    /// <summary>Successful tool state — success green.</summary>
    public static CellStyle ToolOk => C.ToolOkStyle;

    /// <summary>Failed tool state — error red.</summary>
    public static CellStyle ToolError => C.ToolErrorStyle;

    /// <summary>Tool output body — primary text tone.</summary>
    public static CellStyle ToolBody => C.ToolBodyStyle;

    /// <summary>Tool status pill — running (<c>"running"</c>) — warning
    /// yellow. Cell-side projection of
    /// <c>StatusMappers.ToolCallStatusToBrushKey(Running)</c>
    /// (<c>MochaYellow</c>); same color as <see cref="ToolRunning"/>
    /// (glyph style), separate member so the pill can diverge.</summary>
    public static CellStyle ToolPillRunning => C.ToolPillRunningStyle;

    /// <summary>Tool status pill — success (<c>"ok"</c>) — success green.
    /// Cell-side projection of
    /// <c>StatusMappers.ToolCallStatusToBrushKey(Success)</c>
    /// (<c>MochaGreen</c>); same color as <see cref="ToolOk"/>
    /// (glyph style), separate member so the pill can diverge.</summary>
    public static CellStyle ToolPillOk => C.ToolPillOkStyle;

    /// <summary>Tool status pill — error (<c>"err"</c>) — error red.
    /// Cell-side projection of
    /// <c>StatusMappers.ToolCallStatusToBrushKey(Error)</c>
    /// (<c>MochaRed</c>); same color as <see cref="ToolError"/>
    /// (glyph style), separate member so the pill can diverge.</summary>
    public static CellStyle ToolPillError => C.ToolPillErrorStyle;

    /// <summary>Gutters, hints, secondary metadata — SGR dim (terminal-level
    /// muted rendering; keeps goldens stable across truecolor capability).</summary>
    public static CellStyle Dim => C.DimStyle;
}
