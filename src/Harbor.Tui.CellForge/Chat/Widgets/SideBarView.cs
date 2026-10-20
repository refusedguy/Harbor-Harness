using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
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
///     What one MCP server row paints — its glyph and colour, owned by the
///     state (State pattern, #1550). <c>PaintMcp</c> used to carry the
///     mapping as an inline switch over <see cref="McpServerState" />:
///     painting behaviour derived from internal state, every pixel pair
///     living as an arm of the paint method — and each new status meaning a
///     fresh edit to the painter. Now each state owns one instance of this
///     record and the painter asks the state for its row. Adding a status is
///     one enum member plus one table entry below, PaintMcp untouched;
///     pixels are identical to the switch it replaced.
/// </summary>
/// <remarks>
///     The three instances are the pixels; the frozen table is the only
///     state → visual declaration, built once — no per-lookup
///     allocation on the paint hot path. An out-of-range byte throws (fail
///     fast) instead of silently painting as some other state; the byte is
///     only ever produced inside this file, so the throw fires on corrupted
///     data only.
/// </remarks>
public sealed record McpServerStateVisual(char Glyph, PackedColor Color)
{
    /// <summary>Established session: filled dot, success colour.</summary>
    public static readonly McpServerStateVisual Connected = new('●', ChatPalette.Success);

    /// <summary>Handshake in flight: half-filled dot, warning colour.</summary>
    public static readonly McpServerStateVisual Connecting = new('◐', ChatPalette.Warning);

    /// <summary>Unreachable or failed: filled dot, error colour.</summary>
    public static readonly McpServerStateVisual Error = new('●', ChatPalette.Error);

    private static readonly FrozenDictionary<McpServerState, McpServerStateVisual> ByState =
        new Dictionary<McpServerState, McpServerStateVisual>(3)
        {
            [McpServerState.Connected] = Connected,
            [McpServerState.Connecting] = Connecting,
            [McpServerState.Error] = Error,
        }.ToFrozenDictionary();

    /// <summary>The visual this state paints with. An undefined byte throws.</summary>
    public static McpServerStateVisual Of(McpServerState state) => ByState[state];
}

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
/// Named mount point for a sidebar slot (opencode sidebar parity, #1181):
/// <see cref="Content" /> sections flow below the built-in sections (every
/// contributor paints, ordered by <see cref="SideBarSlot.Order" />),
/// <see cref="Footer" /> is single-winner (lowest order wins, registration
/// order breaks ties) pinned to the bottom rows — the same contract as
/// opencode's <c>sidebar_content</c> / <c>sidebar_footer</c> slots.
/// </summary>
public enum SideBarSlotPoint : byte
{
    /// <summary>Flowing section below the built-ins (multi-contributor).</summary>
    Content = 0,

    /// <summary>Pinned bottom block (single winner).</summary>
    Footer = 1,
}

/// <summary>
/// Plugin-extensible sidebar slot (widgets §3.x): a section title plus a
/// pure line provider evaluated per paint. Providers must not allocate
/// heavily — they run every paint frame while the sidebar is visible.
/// <see cref="Point" /> selects the opencode-parity mount point
/// (<c>sidebar_content</c> vs <c>sidebar_footer</c>); <see cref="Order" />
/// sorts contributors (and picks the footer winner). Both default to the
/// pre-#1181 behaviour (flowing content, order 100), so existing call sites
/// — and their goldens — compile and paint unchanged.
/// </summary>
public sealed record SideBarSlot(
    string Title,
    Func<SideBarState, IReadOnlyList<SideBarLine>> Lines,
    SideBarSlotPoint Point = SideBarSlotPoint.Content,
    int Order = 100);

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

    /// <summary>Paints the sidebar into <paramref name="rect" />. Tiny rects are skipped.</summary>
    public static void Paint(ScreenBuffer buffer, Rect rect, SideBarState state, IReadOnlyList<SideBarSlot>? extraSlots = null, IReadOnlyList<SessionRowViewModel>? sessions = null)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(state);
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

        // O12 steal (epic #1155, opencode sidebar.tsx): named slot points —
        // content sections flow, the footer winner pins to the bottom, and an
        // overlay scrollbar marks clipped content without shifting layout.
        // With no slots and no clipping this is a no-op: the paint below is
        // byte-identical to the pre-#1181 path.
        int contentStart = rect.Y + 1;
        painter.PaintContentSlots(state, extraSlots);
        int contentEnd = painter.Y;
        int footerTop = painter.PaintFooterSlot(state, extraSlots);
        painter.PaintOverlayScrollbar(contentStart, contentEnd, footerTop);
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

        /// <summary>
        /// List rows clipped away by caps this frame. Feeds the overlay
        /// scrollbar total (<c>painted + clipped</c>); zero on a fitting
        /// frame, so the scrollbar stays hidden and the buffer untouched.
        /// Item-counted (not row-counted) — the scrollbar is a clipped-below
        /// indicator, and its hidden-iff-fits signal is exact.
        /// </summary>
        public int Clipped;

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

                Clipped += files.Count - fileRows;
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
                    // State pattern (#1550): the state owns its glyph + colour —
                    // the painter asks, it never branches.
                    var visual = McpServerStateVisual.Of(server.State);
                    var style = new CellStyle(visual.Color);
                    if (Y < rect.Bottom - 1)
                    {
                        glyphBuf[0] = visual.Glyph;
                        glyphBuf[1] = ' ';
                        buffer.SetText(labelX + 1, Y, glyphBuf, style);
                        ValueLine(buffer, rect, labelX + 3, Y, innerW - 3, server.Name.AsSpan(), style);
                    }

                    Y++;
                }

                Clipped += servers.Count - serverRows;
            }
        }

        public void PaintSessions(IReadOnlyList<SessionRowViewModel>? sessions)
        {
            // ── Sessions ────────────────────────────────────────────────────────────
            if (sessions is { Count: > 0 })
            {
                Y = Section(buffer, rect, labelX, Y, "SESSIONS", headingStyle);
                int sessionRows = Math.Min(sessions.Count, Math.Max(0, rect.Bottom - 2 - Y));
                // One row buffer for the whole section. The row used to be
                // built as "marker + ' ' + title" — a fresh string per visible
                // session per frame, and the sidebar paints every frame. The
                // buffer is sized by the row budget (the sidebar's inner
                // width), so it is bounded by the viewport and is allocated
                // once per painted section, not once per row.
                int budget = Math.Max(0, innerW - 1);
                Span<char> row = stackalloc char[budget];
                for (int i = 0; i < sessionRows; i++)
                {
                    var session = sessions[i];
                    char marker = session.IsActive ? '▸' : ' ';
                    // The same characters, in the same order, as the
                    // concatenated row: the marker, the separator, and the
                    // title truncated to what the row budget has left.
                    int total = Math.Min(2 + session.Title.Length, budget);
                    if (total > 0)
                    {
                        row[0] = marker;
                        if (total > 1)
                        {
                            row[1] = ' ';
                            int take = Math.Min(session.Title.Length, total - 2);
                            if (take > 0)
                            {
                                session.Title.AsSpan(0, take).CopyTo(row[2..]);
                            }
                        }
                    }

                    var rowStyle = session.IsActive ? valueStyle : labelStyle;
                    Y = ValueLine(buffer, rect, labelX, Y, innerW, row[..total], rowStyle);
                    if (Y < rect.Bottom - 1 && !string.IsNullOrEmpty(session.MetaLine))
                    {
                        Y = ValueLine(buffer, rect, labelX + 2, Y, innerW - 2, session.MetaLine.AsSpan(), labelStyle);
                    }
                }

                Clipped += sessions.Count - sessionRows;
            }
        }

        public void PaintContentSlots(SideBarState state, IReadOnlyList<SideBarSlot>? extraSlots)
        {
            // ── Plugin content slots (opencode sidebar_content) ──────────────
            // Null fast path: no allocation, no paint — the steady-state frame
            // never registers slots, so this must stay free.
            if (extraSlots is null || extraSlots.Count == 0)
            {
                return;
            }

            foreach (var slot in ContentSlotsInOrder(extraSlots))
            {
                PaintSlotSection(state, slot);
            }
        }

        /// <summary>
        /// Paints the footer winner pinned to the bottom rows (opencode
        /// sidebar_footer, single-winner). Paints last among content so the
        /// footer always wins its rows. Returns the first footer row, or
        /// <c>rect.Bottom - 1</c> when no footer slot was contributed.
        /// </summary>
        public int PaintFooterSlot(SideBarState state, IReadOnlyList<SideBarSlot>? extraSlots)
        {
            int limit = rect.Bottom - 1;
            if (extraSlots is null || extraSlots.Count == 0)
            {
                return limit;
            }

            var winner = FooterWinner(extraSlots);
            if (winner is null)
            {
                return limit;
            }

            var lines = winner.Lines(state);
            // Footer height is header + one title/value pair per line. Clamp
            // the pair count so the block never covers the title row —
            // a footer that cannot fit still paints its head, never content.
            int avail = limit - (rect.Y + 1);
            int shown = Math.Min(lines.Count, Math.Max(0, (avail - 1) / 2));
            int footerTop = limit - 1 - (shown * 2);

            int y = Section(buffer, rect, labelX, footerTop, winner.Title, headingStyle);
            for (int i = 0; i < shown; i++)
            {
                var line = lines[i];
                y = ValueLine(buffer, rect, labelX, y, innerW, line.Title, valueStyle);
                ValueLine(buffer, rect, labelX, y, innerW, line.Value, labelStyle);
                y++;
            }

            Clipped += lines.Count - shown;
            return footerTop;
        }

        /// <summary>
        /// Overlay scrollbar over the right-margin column (opencode absolute
        /// scrollbar parity): covers, never shifts — the content width is
        /// untouched, and a fitting frame paints nothing at all, so existing
        /// goldens stay byte-identical. Spans the content region only; the
        /// pinned footer (opencode: outside the scrollbox) is never covered.
        /// </summary>
        public void PaintOverlayScrollbar(int contentStart, int contentEnd, int footerTop)
        {
            int top = Math.Max(contentStart, rect.Y);
            int bottom = Math.Min(footerTop, rect.Bottom - 1);
            int height = bottom - top;
            if (height <= 0)
            {
                return;
            }

            long painted = Math.Max(0, contentEnd - contentStart);
            long total = painted + Math.Max(0, Clipped);
            if (!Scrollbar.IsVisible(total, (int)painted))
            {
                return;
            }

            _ = Scrollbar.TryPaint(buffer, new Rect(rect.Right - 1, top, 1, height), total, 0, (int)painted);
        }

        private void PaintSlotSection(SideBarState state, SideBarSlot slot)
        {
            Y = Section(buffer, rect, labelX, Y, slot.Title, headingStyle);
            var lines = slot.Lines(state);
            int lineRows = Math.Min(lines.Count, Math.Max(0, rect.Bottom - 2 - Y));
            Clipped += lines.Count - lineRows;
            for (int i = 0; i < lineRows; i++)
            {
                var line = lines[i];
                Y = ValueLine(buffer, rect, labelX, Y, innerW, line.Title, valueStyle);
                ValueLine(buffer, rect, labelX, Y, innerW, line.Value, labelStyle);
                Y++;
            }
        }

        private static List<SideBarSlot> ContentSlotsInOrder(IReadOnlyList<SideBarSlot> slots)
        {
            // N is a handful: insertion by Order (strictly-greater shift keeps
            // registration order on ties) beats sort + index bookkeeping, and
            // keeps LINQ off the per-frame path.
            var ordered = new List<SideBarSlot>(slots.Count);
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot is null || slot.Point != SideBarSlotPoint.Content
                    || string.IsNullOrWhiteSpace(slot.Title) || slot.Lines is null)
                {
                    continue;
                }

                int at = ordered.Count;
                while (at > 0 && ordered[at - 1].Order > slot.Order)
                {
                    at--;
                }

                ordered.Insert(at, slot);
            }

            return ordered;
        }

        private static SideBarSlot? FooterWinner(IReadOnlyList<SideBarSlot> slots)
        {
            // Single-winner (opencode mode="single_winner"): lowest Order
            // wins, first registration breaks ties.
            SideBarSlot? winner = null;
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot is null || slot.Point != SideBarSlotPoint.Footer
                    || string.IsNullOrWhiteSpace(slot.Title) || slot.Lines is null)
                {
                    continue;
                }

                if (winner is null || slot.Order < winner.Order)
                {
                    winner = slot;
                }
            }

            return winner;
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
    public static SideBarState ProjectFromStore(UiState state) => Project(state, null);

    /// <summary>
    ///     <see cref="ProjectFromStore(UiState)"/> through a
    ///     <paramref name="cache"/>: the linear active-session scan and the
    ///     session-list array copy are skipped while the fingerprint is unchanged
    ///     (issue #466).
    /// </summary>
    public static SideBarState Project(UiState state, SideBarProjectionCache? cache)
    {
        if (cache is not null && cache.TryGet(state, out SideBarState cached))
        {
            return cached;
        }

        SessionInfo? active = default;
        for (int i = 0; i < state.Chat.Sessions.Length; i++)
        {
            var s = state.Chat.Sessions[i];
            if (s.SessionId == state.Chat.ActiveSessionId)
            {
                active = s;
                break;
            }
        }

        // No ToArray: ImmutableArray<SessionInfo> is already immutable (never
        // mutated in place — the projection cache relies on that too) and it
        // implements IReadOnlyList<SessionInfo>, so handing it over boxes one
        // struct instead of copying N elements on every cache miss.
        IReadOnlyList<SessionInfo>? sessions =
            state.Chat.Sessions.Length == 0 ? null : state.Chat.Sessions;
        var projected = new SideBarState(
            SessionTitle: active?.Title,
            SessionId: active?.SessionId?.Value,
            Model: string.IsNullOrEmpty(state.Chat.Model) ? null : state.Chat.Model,
            TokensIn: state.Chat.Cost.TokensIn,
            TokensOut: state.Chat.Cost.TokensOut,
            CostUsd: (double)state.Chat.Cost.CostUsd,
            ModifiedFiles: null,
            LspErrors: CountLanguageServer(state.Chat.Diagnostics, DiagnosticIssueSeverity.Error),
            LspWarnings: CountLanguageServer(state.Chat.Diagnostics, DiagnosticIssueSeverity.Warning),
            McpServers: null,
            ActiveSessionId: state.Chat.ActiveSessionId,
            Sessions: sessions);

        cache?.Store(state, projected);
        return projected;
    }

    /// <summary>
    ///     How many of the core's diagnostics a language server reported at
    ///     <paramref name="severity" />.
    /// </summary>
    /// <remarks>
    ///     Counted over <see cref="DiagnosticIssueSource.LanguageServer" /> rows
    ///     ONLY, and that restriction is the point. The snapshot also carries
    ///     <see cref="DiagnosticIssueSource.ToolOutput" /> rows — issues a
    ///     detector pulled out of a build log — and those are not what a sidebar
    ///     section labelled after the language server means. Counting them here
    ///     would re-fuse the two concepts #674 separated, in the one place where
    ///     the label makes the difference visible. #674 also found this pair
    ///     hardcoded to <c>0</c>: an empty snapshot and a never-connected path
    ///     then drew the same picture, so a wired-but-silent channel was
    ///     indistinguishable from a working one.
    /// </remarks>
    internal static int CountLanguageServer(
        ImmutableArray<DiagnosticIssue> issues,
        DiagnosticIssueSeverity severity)
    {
        var count = 0;
        for (int i = 0; i < issues.Length; i++)
        {
            DiagnosticIssue issue = issues[i];
            if (issue.Source == DiagnosticIssueSource.LanguageServer && issue.Severity == severity)
            {
                count++;
            }
        }

        return count;
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

/// <summary>
///     Single-slot memo for <see cref="SideBarView.ProjectFromStore"/> (issue #466).
///     <para>
///         The projection walks <c>UiState.Chat.Sessions</c> to find the active
///         session and then copies the whole list into a fresh array. Both are
///         pure functions of the sidebar inputs, and a text delta changes none
///         of them — yet the projection ran on every notification, so a
///         200-session store paid a 200-compare scan plus a 200-element array
///         copy per token. The cache answers from the previous instance while
///         the fingerprint matches.
///     </para>
///     <para>
///         The fingerprint is deliberately wider than (Sessions,
///         ActiveSessionId): the projected <see cref="SideBarState"/> also
///         carries model, token counts and cost, and those DO move on a
///         <c>StepFinishEvent</c>. Caching on the session pair alone would pin
///         a stale cost line. Since #674 the DIAGNOSTICS section counts the
///         core's language-server rows, so the fingerprint carries the
///         diagnostics snapshot too: without it the section would go on
///         reporting whatever it first counted, and a count that cannot change
///         is the same lie as a hardcoded zero wearing a live-looking shape.
///         The session list compares by reference — the
///         immutable array is never mutated in place, so a fresh instance can
///         only mean a change and the same instance proves there was none — and
///         the scalars compare by value. Anything less specific re-projects,
///         which is correct but slower; it can never skip a real change.
///     </para>
///     <para>
///         Not thread-safe by design — the frame that owns the sidebar also owns
///         this cache, so it is only ever touched from the render thread.
///     </para>
/// </summary>
public sealed class SideBarProjectionCache
{
    private bool _has;
    private ImmutableArray<SessionInfo> _sessions;
    private SessionId? _activeSessionId;
    private string? _model;
    private CostSnapshot _cost;
    private ImmutableArray<DiagnosticIssue> _diagnostics;
    private SideBarState? _state;

    /// <summary>Projections actually computed (cache misses) since construction.</summary>
    public int MissCount { get; private set; }

    /// <summary>
    ///     Reference-equal-or-ordinal-equal, allocation-free. The store hands
    ///     out a fresh <see cref="SessionId" /> / model string whenever a
    ///     session sync re-seeds them, so reference identity alone would miss
    ///     on inputs the sidebar renders identically. Deliberately NOT
    ///     <see cref="SessionId" />'s structural <c>Equals</c>: the ValueObject
    ///     base walks a <c>yield</c> iterator, which would put two allocations
    ///     per comparison back on the per-frame path this cache exists to
    ///     protect. <see cref="SessionId" /> has exactly one equality component
    ///     (its string), so ordinal string equality is the same answer.
    /// </summary>
    internal static bool SameId(SessionId? a, SessionId? b) =>
        ReferenceEquals(a, b)
        || (a is not null && b is not null && string.Equals(a.Value, b.Value, StringComparison.Ordinal));

    /// <summary>Same contract as <see cref="SameId" />, for the model string.</summary>
    internal static bool SameText(string? a, string? b) =>
        ReferenceEquals(a, b) || string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>
    ///     The cached projection when <paramref name="state" /> carries the same
    ///     sidebar inputs, otherwise <c>false</c>.
    /// </summary>
    public bool TryGet(UiState state, out SideBarState projected)
    {
        if (_has
            && _sessions.Equals(state.Chat.Sessions)
            && SameId(_activeSessionId, state.Chat.ActiveSessionId)
            && SameText(_model, state.Chat.Model)
            && _cost == state.Chat.Cost
            && _diagnostics.Equals(state.Chat.Diagnostics))
        {
            projected = _state!;
            return true;
        }

        projected = SideBarState.Empty;
        return false;
    }

    /// <summary>Memoize <paramref name="projected" /> for <paramref name="state" />.</summary>
    public void Store(UiState state, SideBarState projected)
    {
        _has = true;
        _sessions = state.Chat.Sessions;
        _activeSessionId = state.Chat.ActiveSessionId;
        _model = state.Chat.Model;
        _cost = state.Chat.Cost;
        _diagnostics = state.Chat.Diagnostics;
        _state = projected;
        MissCount++;
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
