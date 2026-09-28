using System.Text;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.ViewModels;

namespace Harbor.Tui.CellForge.Widgets;

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
/// files, LSP/MCP health, and the full session list. The host refreshes the
/// instance; the sidebar itself holds no state (pure paint function).
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
    IReadOnlyList<McpServerStatus>? McpServers = null,
    SessionId? ActiveSessionId = null,
    IReadOnlyList<SessionInfo>? Sessions = null,
    string? Agent = null,
    int MessageCount = 0,
    int ContextWindow = 0)
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
    public static void Paint(ScreenBuffer buffer, Rect rect, SideBarState state, IReadOnlyList<SideBarSlot>? extraSlots = null, IReadOnlyList<SessionRowViewModel>? sessions = null)
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

        // Panel title row (chrome pattern: thin title + rule, same as INPUT):
        // row 0 was blank padding — sections still start at rect.Y + 1.
        PanelChrome.PaintTitleRow(buffer, labelX, rect.Y, innerW, PanelChrome.SidebarTitle);

        int y = rect.Y + 1;
        if (y >= rect.Bottom)
        {
            return;
        }

        var painter = new SidebarPainter(buffer, rect, labelX, innerW, labelStyle, valueStyle, headingStyle) { Y = y };

        painter.PaintSession(state);

        painter.PaintModel(state);

        painter.PaintAgent(state);

        painter.PaintTokens(state);

        painter.PaintModifiedFiles(state);

        painter.PaintDiagnostics(state);

        painter.PaintMcp(state);

        painter.PaintSessions(sessions);

        painter.PaintPluginSlots(state, extraSlots);
    }

    /// <summary>Stateful section painter threading the row cursor through sidebar sections.</summary>
    /// <remarks>Struct (not class): instantiated per frame on the paint hot
    /// path, a class here cost 88 B/frame and broke the allocation-free
    /// steady-frame budget (RendererMoatPerfTests).</remarks>
    private struct SidebarPainter(
        ScreenBuffer buffer,
        Rect rect,
        int labelX,
        int innerW,
        CellStyle labelStyle,
        CellStyle valueStyle,
        CellStyle headingStyle)
    {
        public int Y;

        public void PaintSession(SideBarState state)
        {
            // ── Session ────────────────────────────────────────────────────────
            Y = Section(buffer, rect, labelX, Y, "SESSION", headingStyle);
            Y = ValueLine(buffer, rect, labelX, Y, innerW, (state.SessionTitle ?? "(no session)").AsSpan(), valueStyle);
            Y = ValueLine(buffer, rect, labelX, Y, innerW, ShortId(state.SessionId), labelStyle);
            if (state.MessageCount > 0)
            {
                Span<char> msgBuf = stackalloc char[16];
                int msgLen = FormatWithSuffix(state.MessageCount, " msgs", msgBuf);
                Y = ValueLine(buffer, rect, labelX, Y, innerW, msgBuf[..msgLen], labelStyle);
            }
        }

        public void PaintModel(SideBarState state)
        {
            // ── Model ──────────────────────────────────────────────────────────
            Y = Section(buffer, rect, labelX, Y, "MODEL", headingStyle);
            Y = ValueLine(buffer, rect, labelX, Y, innerW, (state.Model ?? "—").AsSpan(), valueStyle);
        }

        public void PaintAgent(SideBarState state)
        {
            // ── Agent ──────────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(state.Agent))
            {
                Y = Section(buffer, rect, labelX, Y, "AGENT", headingStyle);
                Y = ValueLine(buffer, rect, labelX, Y, innerW, state.Agent.AsSpan(), valueStyle);
            }
        }

        public void PaintTokens(SideBarState state)
        {
            // ── Tokens ─────────────────────────────────────────────────────────
            Y = Section(buffer, rect, labelX, Y, "TOKENS", headingStyle);
            var tokenStyle = new CellStyle(ChatPalette.Muted);
            Span<char> tokenBuf = stackalloc char[24];
            int tokenLen = FormatTokensLine(state.TokensIn, state.TokensOut, tokenBuf);
            Y = ValueLine(buffer, rect, labelX, Y, innerW, tokenBuf[..tokenLen], tokenStyle);
            if (state.CostUsd > 0)
            {
                Span<char> costBuf = stackalloc char[16];
                int costLen = FormatCostUsd(state.CostUsd, costBuf);
                Y = ValueLine(buffer, rect, labelX, Y, innerW, costBuf[..costLen], tokenStyle);
            }

            if (state.ContextWindow > 0)
            {
                // Canonical #75 definition: accumulated input+output over the window.
                int pct = ContextUsage.PercentUsed(state.TokensIn, state.TokensOut, state.ContextWindow);
                Span<char> ctxBuf = stackalloc char[16];
                int ctxLen = 0;
                "ctx ".AsSpan().CopyTo(ctxBuf);
                ctxLen += 4;
                ctxLen += AppendDigits(pct, ctxBuf[ctxLen..]);
                if (ctxLen < ctxBuf.Length)
                {
                    ctxBuf[ctxLen++] = '%';
                }

                Y = ValueLine(buffer, rect, labelX, Y, innerW, ctxBuf[..ctxLen], tokenStyle);
            }
        }

        public void PaintModifiedFiles(SideBarState state)
        {
            // ── Modified files ─────────────────────────────────────────────────
            var files = state.ModifiedFiles;
            if (files is { Count: > 0 })
            {
                Span<char> modBuf = stackalloc char[32];
                int modLen = FormatSectionTitle("MODIFIED (", files.Count, ')', modBuf);
                Y = SectionSpan(buffer, rect, labelX, Y, modBuf[..modLen], headingStyle);
                int fileRows = Math.Min(files.Count, Math.Max(0, rect.Bottom - 2 - Y));
                for (int i = 0; i < fileRows; i++)
                {
                    Y = ValueLine(buffer, rect, labelX, Y, innerW, files[i].AsSpan(), valueStyle);
                }
            }
        }

        public void PaintDiagnostics(SideBarState state)
        {
            // ── LSP ────────────────────────────────────────────────────────────
            if (state.LspErrors > 0 || state.LspWarnings > 0)
            {
                Y = Section(buffer, rect, labelX, Y, "DIAGNOSTICS", headingStyle);
                var errStyle = new CellStyle(state.LspErrors > 0 ? ChatPalette.Error : ChatPalette.Muted);
                var warnStyle = new CellStyle(state.LspWarnings > 0 ? ChatPalette.Warning : ChatPalette.Muted);
                Span<char> diagBuf = stackalloc char[24];
                int errLen = FormatWithSuffix(state.LspErrors, " errors", diagBuf);
                Y = ValueLine(buffer, rect, labelX, Y, innerW, diagBuf[..errLen], errStyle);
                int warnLen = FormatWithSuffix(state.LspWarnings, " warnings", diagBuf);
                Y = ValueLine(buffer, rect, labelX, Y, innerW, diagBuf[..warnLen], warnStyle);
            }
        }

        public void PaintMcp(SideBarState state)
        {
            // ── MCP ────────────────────────────────────────────────────────────
            var servers = state.McpServers;
            if (servers is { Count: > 0 })
            {
                Y = Section(buffer, rect, labelX, Y, "MCP", headingStyle);
                int serverRows = Math.Min(servers.Count, Math.Max(0, rect.Bottom - 2 - Y));
                Span<char> glyphBuf = stackalloc char[2];
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
                    if (Y < rect.Bottom - 1)
                    {
                        glyphBuf[0] = glyph;
                        glyphBuf[1] = ' ';
                        buffer.SetText(labelX + 1, Y, glyphBuf, style);
                        ValueLine(buffer, rect, labelX + 3, Y, innerW - 3, server.Name.AsSpan(), style);
                    }

                    Y++;
                }
            }
        }

        public void PaintSessions(IReadOnlyList<SessionRowViewModel>? sessions)
        {
            // ── Sessions ────────────────────────────────────────────────────────────
            if (sessions is { Count: > 0 })
            {
                Y = Section(buffer, rect, labelX, Y, "SESSIONS", headingStyle);
                int sessionRows = Math.Min(sessions.Count, Math.Max(0, rect.Bottom - 2 - Y));
                for (int i = 0; i < sessionRows; i++)
                {
                    var session = sessions[i];
                    string marker = session.IsActive ? "▸" : " ";
                    string display = marker + " " + session.Title;
                    var rowStyle = session.IsActive ? valueStyle : labelStyle;
                    Y = ValueLine(buffer, rect, labelX, Y, innerW, display.AsSpan(), rowStyle);
                    if (Y < rect.Bottom - 1 && !string.IsNullOrEmpty(session.MetaLine))
                    {
                        Y = ValueLine(buffer, rect, labelX + 2, Y, innerW - 2, session.MetaLine.AsSpan(), labelStyle);
                    }
                }
            }
        }

        public void PaintPluginSlots(SideBarState state, IReadOnlyList<SideBarSlot>? extraSlots)
        {
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

                    Y = Section(buffer, rect, labelX, Y, slot.Title, headingStyle);
                    var lines = slot.Lines(state);
                    int lineRows = Math.Min(lines.Count, Math.Max(0, rect.Bottom - 2 - Y));
                    for (int i = 0; i < lineRows; i++)
                    {
                        var line = lines[i];
                        Y = ValueLine(buffer, rect, labelX, Y, innerW, line.Title, valueStyle);
                        ValueLine(buffer, rect, labelX, Y, innerW, line.Value, labelStyle);
                        Y++;
                    }
                }
            }
        }
        private static int Section(ScreenBuffer buffer, Rect rect, int x, int y, string title, CellStyle style)
        {
            if (y >= rect.Bottom - 1)
            {
                return y;
            }

            // Glyph + literal written as two spans — an interpolated string here
            // would allocate on every frame (sidebar paints each frame).
            int innerW = rect.Width - 3;
            Span<char> dot = [SectionDot, ' '];
            buffer.SetText(x, y, dot, style);
            buffer.SetText(x + 2, y, title.AsSpan(0, Math.Min(title.Length, Math.Max(0, innerW - 2))), style);
            return y + 1;
        }

        private static int SectionSpan(ScreenBuffer buffer, Rect rect, int x, int y, ReadOnlySpan<char> title, CellStyle style)
        {
            if (y >= rect.Bottom - 1)
            {
                return y;
            }

            int innerW = rect.Width - 3;
            Span<char> dot = [SectionDot, ' '];
            buffer.SetText(x, y, dot, style);
            buffer.SetText(x + 2, y, title[..Math.Min(title.Length, Math.Max(0, innerW - 2))], style);
            return y + 1;
        }

        private static int ValueLine(ScreenBuffer buffer, Rect rect, int x, int y, int innerW, ReadOnlySpan<char> value, CellStyle style)
        {
            if (y >= rect.Bottom - 1)
            {
                return y;
            }

            buffer.SetText(x + 1, y, value[..Math.Min(value.Length, Math.Max(0, innerW - 1))], style);
            return y + 1;
        }

        private static ReadOnlySpan<char> ShortId(string? id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return ReadOnlySpan<char>.Empty;
            }

            return id.AsSpan(0, Math.Min(8, id.Length));
        }

        /// <summary>Writes «<paramref name="prefix" /><paramref name="value" /><paramref name="suffix" />» into the buffer; returns length.</summary>
        private static int FormatSectionTitle(string prefix, int value, char suffix, Span<char> into)
        {
            int len = 0;
            prefix.AsSpan().CopyTo(into[len..]);
            len += prefix.Length;
            len += AppendDigits(value, into[len..]);
            if (len < into.Length)
            {
                into[len++] = suffix;
            }

            return Math.Min(len, into.Length);
        }

        /// <summary>Writes digits followed by a literal suffix («12 errors»); returns length.</summary>
        private static int FormatWithSuffix(int value, string suffix, Span<char> into)
        {
            int len = AppendDigits(value, into);
            suffix.AsSpan().CopyTo(into[len..]);
            len += suffix.Length;
            return Math.Min(len, into.Length);
        }

        /// <summary>
        /// Allocation-free twin of the interpolated token line
        /// «<c>{in}↑ {out}↓</c>»: both figures formatted into one buffer so the
        /// every-frame sidebar paint stays zero-alloc. Returns the written length.
        /// </summary>
        private static int FormatTokensLine(long tokensIn, long tokensOut, Span<char> into)
        {
            int len = FormatTokensTo(tokensIn, into);
            if (len < into.Length)
            {
                into[len++] = '\u2191';
            }

            if (len < into.Length)
            {
                into[len++] = ' ';
            }

            len += FormatTokensTo(tokensOut, into[len..]);
            if (len < into.Length)
            {
                into[len++] = '\u2193';
            }

            return len;
        }

        /// <summary>Span twin of <see cref="FormatTokens" /> — same figures, no string.</summary>
        private static int FormatTokensTo(long tokens, Span<char> into)
        {
            if (into.Length == 0)
            {
                return 0;
            }

            if (tokens < 1_000)
            {
                return AppendDigits(tokens, into);
            }

            double scaled;
            char suffix;
            if (tokens < 1_000_000)
            {
                scaled = tokens / 1000.0;
                suffix = 'k';
            }
            else
            {
                scaled = tokens / 1_000_000.0;
                suffix = 'M';
            }

            // "0.#": one decimal, dropped when integral.
            long whole = (long)scaled;
            int len = AppendDigits(whole, into);
            int tenth = (int)Math.Round((scaled - whole) * 10);
            if (tenth > 0 && len < into.Length)
            {
                into[len++] = '.';
                into[len++] = (char)('0' + Math.Min(9, tenth));
            }

            if (len < into.Length)
            {
                into[len++] = suffix;
            }

            return len;
        }

        /// <summary>Writes <paramref name="value" /> in decimal digits; returns length.</summary>
        private static int AppendDigits(long value, Span<char> into)
        {
            if (value == 0)
            {
                if (into.Length > 0)
                {
                    into[0] = '0';
                    return 1;
                }

                return 0;
            }

            int len = 0;
            while (value > 0 && len < into.Length)
            {
                into[len++] = (char)('0' + value % 10);
                value /= 10;
            }

            into[..len].Reverse();
            return len;
        }

        /// <summary>
        /// Span twin of <c>cost.ToString("0.####")</c> prefixed with «$»: integer
        /// part plus up to four decimals without trailing zeros, invariant style.
        /// </summary>
        private static int FormatCostUsd(double cost, Span<char> into)
        {
            if (into.Length == 0)
            {
                return 0;
            }

            into[0] = '$';
            double clamped = Math.Max(0, cost);
            long whole = (long)clamped;
            long frac = (long)Math.Round((clamped - whole) * 10_000);
            if (frac >= 10_000)
            {
                whole++; // rounding carried into the integer part
                frac = 0;
            }

            int len = 1 + AppendDigits(whole, into[1..]);
            if (frac <= 0)
            {
                return len;
            }

            Span<char> fracDigits = stackalloc char[4];
            for (int i = 3; i >= 0; i--)
            {
                fracDigits[i] = (char)('0' + frac % 10);
                frac /= 10;
            }

            int significant = 4;
            while (significant > 0 && fracDigits[significant - 1] == '0')
            {
                significant--;
            }

            if (len < into.Length)
            {
                into[len++] = '.';
            }

            for (int i = 0; i < significant && len < into.Length; i++)
            {
                into[len++] = fracDigits[i];
            }

            return len;
        }
    }

    /// <summary>42-column sidebar area docked to the right edge above the status row.</summary>
    public static Rect Area(int terminalWidth, int terminalHeight) =>
        new(terminalWidth - SideBarLayout.DefaultWidth, 0, SideBarLayout.DefaultWidth, Math.Max(0, terminalHeight - 1));

    /// <summary>
    ///     Projects <see cref="UiState"/> session data into a <see cref="SideBarState"/>.
    ///     The active session's chrome (title, id, model, tokens, cost) populates the
    ///     top-level sidebar fields; the full session list is attached for the
    ///     sessions section rendered by <see cref="Paint"/>.
    /// </summary>
    public static SideBarState ProjectFromStore(UiState state)
    {
        SessionInfo? active = default;
        foreach (var s in state.Chat.Sessions)
        {
            if (s.SessionId == state.Chat.ActiveSessionId)
            {
                active = s;
                break;
            }
        }

        var sessions = state.Chat.Sessions.Length == 0 ? null : state.Chat.Sessions.ToArray();
        return new SideBarState(
            SessionTitle: active?.Title,
            SessionId: active?.SessionId?.Value,
            Model: string.IsNullOrEmpty(state.Chat.Model) ? null : state.Chat.Model,
            TokensIn: state.Chat.Cost.TokensIn,
            TokensOut: state.Chat.Cost.TokensOut,
            CostUsd: (double)state.Chat.Cost.CostUsd,
            ModifiedFiles: null,
            LspErrors: 0,
            LspWarnings: 0,
            McpServers: null,
            ActiveSessionId: state.Chat.ActiveSessionId,
            Sessions: sessions);
    }

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
