using System.Text;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>MCP server connectivity for the sidebar widget.</summary>
public enum McpServerState : byte
{
    Connected = 0,
    Connecting = 1,
    Error = 2,
}

/// <summary>One MCP server row of the sidebar.</summary>
public sealed record McpServerStatus(string Name, McpServerState State);

/// <summary>
/// Immutable sidebar snapshot — session info, token counter, model, modified
/// files, LSP/MCP health. The host refreshes the instance; the sidebar itself
/// holds no state (pure paint function).
/// </summary>
public sealed record SideBarState(
    string? SessionTitle = null,
    string? SessionId = null,
    string? Model = null,
    long TokensIn = 0,
    long TokensOut = 0,
    double CostUsd = 0,
    IReadOnlyList<string>? ModifiedFiles = null,
    int LspErrors = 0,
    int LspWarnings = 0,
    IReadOnlyList<McpServerStatus>? McpServers = null)
{
    /// <summary>Static «nothing to show» snapshot.</summary>
    public static readonly SideBarState Empty = new();
}

/// <summary>One plugin-contributed sidebar line (title row → value row).</summary>
public sealed record SideBarLine(string Title, string Value);

/// <summary>
/// Plugin-extensible sidebar slot (widgets §3.x): a section title plus a
/// pure line provider evaluated per paint. Providers must not allocate
/// heavily — they run every paint frame while the sidebar is visible.
/// </summary>
public sealed record SideBarSlot(string Title, Func<SideBarState, IReadOnlyList<SideBarLine>> Lines);

/// <summary>
/// Right sidebar (OpenCode/Kilo pattern): a 42-column context panel shown
/// automatically on wide terminals. Pure paint over <see cref="SideBarState" />
/// — the host decides placement via <see cref="SideBarLayout.ComputeArea" />
/// and refreshes state; the view draws session info, token counter, model,
/// modified files, LSP health, MCP servers, and any registered slots.
/// </summary>
public static class SideBarView
{
    private const char SectionDot = '●';
    private const char ServerConnected = '●';
    private const char ServerConnecting = '◐';
    private const char ServerError = '●';

    /// <summary>Paints the sidebar into <paramref name="rect" />. Tiny rects are skipped.</summary>
    public static void Paint(ScreenBuffer buffer, Rect rect, SideBarState state, IReadOnlyList<SideBarSlot>? extraSlots = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (rect.Width < 12 || rect.Height < 6 || rect.X >= buffer.Cols || rect.Y >= buffer.Rows)
        {
            return;
        }

        rect = ClampTo(buffer, rect);
        var fillStyle = new CellStyle(ChatPalette.Panel);
        buffer.Fill(rect, Cell.From(new Rune(' '), fillStyle));

        // Left hairline — the sidebar's separator from the chat column.
        var borderStyle = new CellStyle(ChatPalette.Border);
        for (int row = rect.Y; row < rect.Bottom; row++)
        {
            buffer.At(rect.X, row) = Cell.From(new Rune('│'), borderStyle);
        }

        int labelX = rect.X + 2;
        int innerW = rect.Width - 3; // border + padding + right margin
        var labelStyle = ChatPalette.Dim;
        var valueStyle = ChatPalette.ToolArgs;
        var headingStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);

        int y = rect.Y + 1;
        if (y >= rect.Bottom)
        {
            return;
        }

        // ── Session ────────────────────────────────────────────────────────
        y = Section(buffer, rect, labelX, y, "SESSION", headingStyle);
        y = ValueLine(buffer, rect, labelX, y, innerW, state.SessionTitle ?? "(no session)", valueStyle);
        y = ValueLine(buffer, rect, labelX, y, innerW, ShortId(state.SessionId), labelStyle);

        // ── Model ──────────────────────────────────────────────────────────
        y = Section(buffer, rect, labelX, y, "MODEL", headingStyle);
        y = ValueLine(buffer, rect, labelX, y, innerW, state.Model ?? "—", valueStyle);

        // ── Tokens ─────────────────────────────────────────────────────────
        y = Section(buffer, rect, labelX, y, "TOKENS", headingStyle);
        var tokenStyle = new CellStyle(ChatPalette.Muted);
        y = ValueLine(buffer, rect, labelX, y, innerW,
            $"{FormatTokens(state.TokensIn)}↑ {FormatTokens(state.TokensOut)}↓", tokenStyle);
        if (state.CostUsd > 0)
        {
            y = ValueLine(buffer, rect, labelX, y, innerW,
                "$" + state.CostUsd.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), tokenStyle);
        }

        // ── Modified files ─────────────────────────────────────────────────
        var files = state.ModifiedFiles;
        if (files is { Count: > 0 })
        {
            y = Section(buffer, rect, labelX, y, $"MODIFIED ({files.Count})", headingStyle);
            int fileRows = Math.Min(files.Count, Math.Max(0, rect.Bottom - 2 - y));
            for (int i = 0; i < fileRows; i++)
            {
                y = ValueLine(buffer, rect, labelX, y, innerW, files[i], valueStyle);
            }
        }

        // ── LSP ────────────────────────────────────────────────────────────
        if (state.LspErrors > 0 || state.LspWarnings > 0)
        {
            y = Section(buffer, rect, labelX, y, "DIAGNOSTICS", headingStyle);
            var errStyle = new CellStyle(state.LspErrors > 0 ? ChatPalette.Error : ChatPalette.Muted);
            var warnStyle = new CellStyle(state.LspWarnings > 0 ? ChatPalette.Warning : ChatPalette.Muted);
            y = ValueLine(buffer, rect, labelX, y, innerW, $"{state.LspErrors} errors", errStyle);
            y = ValueLine(buffer, rect, labelX, y, innerW, $"{state.LspWarnings} warnings", warnStyle);
        }

        // ── MCP ────────────────────────────────────────────────────────────
        var servers = state.McpServers;
        if (servers is { Count: > 0 })
        {
            y = Section(buffer, rect, labelX, y, "MCP", headingStyle);
            int serverRows = Math.Min(servers.Count, Math.Max(0, rect.Bottom - 2 - y));
            for (int i = 0; i < serverRows; i++)
            {
                var server = servers[i];
                (char glyph, PackedColor color) = server.State switch
                {
                    McpServerState.Connected => (ServerConnected, ChatPalette.Success),
                    McpServerState.Connecting => (ServerConnecting, ChatPalette.Warning),
                    _ => (ServerError, ChatPalette.Error),
                };
                var style = new CellStyle(color);
                var text = $"{glyph} {server.Name}";
                ValueLine(buffer, rect, labelX, y, innerW, text, style);
                y++;
            }
        }

        // ── Plugin slots ───────────────────────────────────────────────────
        if (extraSlots is not null)
        {
            for (int s = 0; s < extraSlots.Count; s++)
            {
                var slot = extraSlots[s];
                if (slot is null || string.IsNullOrWhiteSpace(slot.Title) || slot.Lines is null)
                {
                    continue;
                }

                y = Section(buffer, rect, labelX, y, slot.Title, headingStyle);
                var lines = slot.Lines(state);
                int lineRows = Math.Min(lines.Count, Math.Max(0, rect.Bottom - 2 - y));
                for (int i = 0; i < lineRows; i++)
                {
                    var line = lines[i];
                    y = ValueLine(buffer, rect, labelX, y, innerW, line.Title, valueStyle);
                    ValueLine(buffer, rect, labelX, y, innerW, line.Value, labelStyle);
                    y++;
                }
            }
        }
    }

    /// <summary>42-column sidebar area docked to the right edge above the status row.</summary>
    public static Rect Area(int terminalWidth, int terminalHeight) =>
        new(terminalWidth - SideBarLayout.DefaultWidth, 0, SideBarLayout.DefaultWidth, Math.Max(0, terminalHeight - 1));

    /// <summary>Compact token figure: 999 → «999», 12 345 → «12.3k», 1 234 567 → «1.2M».</summary>
    public static string FormatTokens(long tokens) => tokens switch
    {
        < 1_000 => tokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
        < 1_000_000 => (tokens / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k",
        _ => (tokens / 1_000_000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M",
    };

    private static Rect ClampTo(ScreenBuffer buffer, Rect rect)
    {
        int width = Math.Min(rect.Width, buffer.Cols - rect.X);
        int height = Math.Min(rect.Height, buffer.Rows - rect.Y);
        return new Rect(rect.X, rect.Y, Math.Max(0, width), Math.Max(0, height));
    }

    private static int Section(ScreenBuffer buffer, Rect rect, int x, int y, string title, CellStyle style)
    {
        if (y >= rect.Bottom - 1)
        {
            return y;
        }

        int innerW = rect.Width - 3;
        var text = $"{SectionDot} {title}".AsSpan(0, Math.Min(title.Length + 2, innerW));
        buffer.SetText(x, y, text, style);
        return y + 1;
    }

    private static int ValueLine(ScreenBuffer buffer, Rect rect, int x, int y, int innerW, string value, CellStyle style)
    {
        if (y >= rect.Bottom - 1)
        {
            return y;
        }

        buffer.SetText(x + 1, y, value.AsSpan(0, Math.Min(value.Length, innerW - 1)), style);
        return y + 1;
    }

    private static string ShortId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return string.Empty;
        }

        return id.Length <= 8 ? id : id[..8];
    }
}

/// <summary>Sidebar placement policy (Kilo/OpenCode pattern).</summary>
public static class SideBarLayout
{
    /// <summary>Default sidebar width in columns.</summary>
    public const int DefaultWidth = 42;

    /// <summary>Auto-show threshold: terminals narrower than this keep the single column.</summary>
    public const int AutoShowMinWidth = 120;

    /// <summary>True when the terminal is wide enough to dock the sidebar.</summary>
    public static bool ShouldShow(int terminalWidth) => terminalWidth >= AutoShowMinWidth;
}
