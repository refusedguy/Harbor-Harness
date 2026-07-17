using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions.State;
using System.Collections.Immutable;
using Spectre.Console;
using Spectre.Tui;

namespace Harbor.Tui.SpectreTui.Helpers;

/// <summary>Which region currently owns the keyboard.</summary>
public enum FocusMode
{
    Input,
    Chat
}

/// <summary>
///     Builds the chat screen widget tree from the shared, renderer-agnostic
///     <see cref="UiState" /> each frame, using real Spectre.TUI widgets
///     (ScrollViewWidget, BoxWidget, SpinnerWidget, HelpWidget, Layout).
///     Returns one widget per named <see cref="Layout" /> region; the screen
///     resolves the region rectangle and renders each widget into it.
/// </summary>
/// <remarks>
///     <para>
///         Stateless beyond the per-frame projection values set by the screen in
///         <c>SyncLayout</c>. No agent logic, no <c>switch (AgentEvent)</c>, no
///         <c>IAgent</c> reference.
///     </para>
/// </remarks>
internal sealed class LayoutBuilder
{
    public string Status { get; set; } = "idle";
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Agent { get; set; } = string.Empty;
    public bool IsReadingInput { get; set; }
    public bool IsStreaming { get; set; }
    public string StreamBuffer { get; set; } = string.Empty;
    public string ThinkBuffer { get; set; } = string.Empty;
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public decimal Cost { get; set; }
    public string InputText { get; set; } = string.Empty;

    /// <summary>Which region currently has keyboard focus.</summary>
    public FocusMode Focus { get; set; } = FocusMode.Input;

    /// <summary>Pre-rendered footer line (assembled by the screen from the keymap).</summary>
    public string FooterText { get; set; } = string.Empty;

    private ImmutableArray<ChatLine> _lines = ImmutableArray<ChatLine>.Empty;
    private int _scrollOffset;
    private int _viewportLines;
    private int _totalLines;

    public LayoutBuilder() { }

    /// <summary>Current scroll-back offset (0 = pinned to newest line, grows toward the top).</summary>
    public int ScrollOffset => _scrollOffset;

    /// <summary>Number of history rows currently visible (set each frame by the screen).</summary>
    public int ViewportLines => _viewportLines;

    /// <summary>Total number of wrapped history rows in the transcript.</summary>
    public int TotalLines => _totalLines;

    /// <summary>True when the user is viewing the newest line (tail-follow).</summary>
    public bool IsPinnedToBottom => _scrollOffset == 0;

    /// <summary>How far the history is scrolled, as a percentage (0 = bottom, 100 = top).</summary>
    public int ScrollPercent
    {
        get
        {
            int max = Math.Max(0, _totalLines - _viewportLines);
            if (max == 0) return 0;
            // _scrollOffset grows toward the top, so flip it for a top-anchored percentage.
            return (int)Math.Round(100.0 * (_totalLines - _viewportLines - _scrollOffset) / max);
        }
    }

    /// <summary>Maximum scroll-back offset given the current viewport and content.</summary>
    private int MaxScroll => Math.Max(0, _totalLines - _viewportLines);

    /// <summary>Clamp the offset to the valid range and return it.</summary>
    private int ClampOffset(int offset) => Math.Clamp(offset, 0, MaxScroll);

    /// <summary>Scroll the history by a relative number of lines (positive = up/back, negative = down/toward newest).</summary>
    public void ScrollBy(int lines) => _scrollOffset = ClampOffset(_scrollOffset + lines);

    /// <summary>Scroll up (back in history) by <paramref name="lines" /> rows.</summary>
    public void ScrollUp(int lines = 1) => ScrollBy(+Math.Max(0, lines));

    /// <summary>Scroll down (toward newest) by <paramref name="lines" /> rows.</summary>
    public void ScrollDown(int lines = 1) => ScrollBy(-Math.Max(0, lines));

    /// <summary>Page up with a 2-line overlap so reading continues seamlessly like a browser.</summary>
    public void PageUp() => ScrollBy(+(Math.Max(1, _viewportLines - 2)));

    /// <summary>Page down with a 2-line overlap so reading continues seamlessly like a browser.</summary>
    public void PageDown() => ScrollBy(-(Math.Max(1, _viewportLines - 2)));

    /// <summary>Jump to the very top of the transcript.</summary>
    public void ScrollToTop() => _scrollOffset = MaxScroll;

    /// <summary>Reset scroll to the newest line (tail-follow).</summary>
    public void ScrollToBottom() => _scrollOffset = 0;

    /// <summary>
    ///     Project the shared UI state (transcript + live streaming) into the
    ///     builder's per-frame values. Called once per render from the screen.
    /// </summary>
    public void SetLines(ImmutableArray<ChatLine> lines, bool isStreaming, ActiveMessage active)
    {
        _lines = lines;
        IsStreaming = isStreaming;
        StreamBuffer = active.TextBuffer;
        ThinkBuffer = active.ThinkBuffer;
    }

    public Layout Layout { get; } = new Layout("Root").SplitRows(
        new Layout("Header").Size(1),
        new Layout("History"),
        new Layout("Status").Size(1),
        new Layout("Spinner").Size(1),
        new Layout("Input").Size(3),
        new Layout("Footer").Size(1));

    public IReadOnlyDictionary<string, IWidget> BuildWidgets(int historyHeight)
    {
        var header = new Paragraph()
            .Style(new Style(Color.Cyan, null, Decoration.Bold))
            .Alignment(Justify.Left);
        string title = string.IsNullOrEmpty(Provider)
            ? "⚓ Harbor"
            : $"⚓ Harbor — {Escape(Provider)}/{Escape(Model)} ({Escape(Agent)})";
        header.Lines.Add(TextLine.FromMarkup($"[bold cyan]{Escape(title)}[/]"));

        var statusLine = new Paragraph()
            .Alignment(Justify.Left);
        statusLine.Lines.Add(TextLine.FromMarkup(
            $"[grey]{Escape(Provider)}/{Escape(Model)} | {Escape(Agent)} | {TokensIn}↑ {TokensOut}↓ | ${Cost:F4} | {Status}[/]"));

        var spinner = IsStreaming
            ? (IWidget)new Paragraph(TextLine.FromMarkup("[cyan]⏳ generating…[/]")).LeftAligned()
            : new SpinnerWidget { Kind = SpinnerKind.Dots };

        string inputText = string.IsNullOrEmpty(InputText) && IsReadingInput
            ? "[dim]type a message, or /help[/]"
            : Escape(InputText);
        // Highlight the input box when it owns focus; dim it otherwise so the
        // user can always tell where keystrokes will land.
        Color inputColor = Focus == FocusMode.Input ? Color.Green : Color.Grey;
        string focusMark = Focus == FocusMode.Input ? "[green]>[/]" : "[grey]>[/]";
        var inputBox = new BoxWidget()
            .Border(Border.Rounded)
            .Style(new Style(inputColor))
            .MarkupTitle(focusMark)
            .Inner(new Paragraph(TextLine.FromMarkup(inputText)).Alignment(Justify.Left));

        var footer = Paragraph.FromMarkup(FooterText).Centered();

        return new Dictionary<string, IWidget>
        {
            ["Header"] = header,
            ["History"] = BuildHistory(historyHeight),
            ["Status"] = statusLine,
            ["Spinner"] = spinner,
            ["Input"] = inputBox,
            ["Footer"] = footer
        };
    }

    private IWidget BuildHistory(int maxHeight)
    {
        var lines = new List<TextLine>();

        void AppendRole(ChatRole role, string content)
        {
            foreach (var block in RoleLines(role, content))
                lines.Add(block);
            lines.Add(Blank());
        }

        foreach (var entry in _lines)
            AppendRole(entry.Role, entry.Text);

        if (IsStreaming)
        {
            if (!string.IsNullOrEmpty(ThinkBuffer))
                AppendRole(ChatRole.Thinking, ThinkBuffer.Trim());
            if (!string.IsNullOrEmpty(StreamBuffer))
                AppendRole(ChatRole.Assistant, StreamBuffer.Trim());
        }

        if (lines.Count == 0)
            lines.Add(TextLine.FromMarkup("[dim]no messages yet…[/]"));

        // Track content + viewport so the screen can clamp scrolling and report
        // a scroll percentage. `maxHeight <= 0` means the area is unavailable
        // yet — keep everything and avoid clamping on the first frame.
        _totalLines = lines.Count;
        _viewportLines = maxHeight;

        // Tail-follow: when not scrolled up, show the last `maxHeight` lines.
        // `maxHeight <= 0` means the area is unavailable yet — show everything.
        if (maxHeight > 0 && lines.Count > maxHeight)
        {
            int skip = Math.Max(0, lines.Count - maxHeight - _scrollOffset);
            if (skip > 0)
                lines = lines.Skip(skip).ToList();
        }

        // Plain Text widget wraps naturally to the region width; no ScrollViewWidget
        // needed for a chat tail view.
        var paragraph = new Paragraph().Alignment(Justify.Left);
        paragraph.Lines.AddRange(lines);
        return paragraph;
    }

    private static TextLine Blank() => TextLine.FromMarkup("");

    private static Color RoleColor(ChatRole role) => role switch
    {
        ChatRole.User => Color.Green,
        ChatRole.Assistant => Color.Aqua,
        ChatRole.Tool => Color.Blue,
        ChatRole.ToolResult => Color.Grey,
        ChatRole.Thinking => Color.Grey,
        ChatRole.System => Color.Grey,
        ChatRole.Error => Color.Red,
        _ => Color.White
    };

    private static string RolePrefix(ChatRole role) => role switch
    {
        ChatRole.User => "> ",
        ChatRole.Assistant => ": ",
        ChatRole.Tool => "→ ",
        ChatRole.ToolResult => "  ",
        ChatRole.Thinking => "💭 ",
        ChatRole.System => "• ",
        ChatRole.Error => "✗ ",
        _ => "  "
    };

    private static IEnumerable<TextLine> RoleLines(ChatRole role, string content)
    {
        var color = RoleColor(role);
        string prefix = RolePrefix(role);
        // Normalize literal "\n" from serialized content into real newlines.
        string body = (content ?? string.Empty).Replace("\\n", "\n");
        string[] segments = body.Split('\n');
        var result = new List<TextLine>();
        for (int i = 0; i < segments.Length; i++)
        {
            var line = new TextLine();
            // The role prefix is our own markup; the body is raw agent output that
            // may contain '[' or ']' (code, tables) and must NOT be parsed as markup.
            line.Spans.Add(new TextSpan(i == 0 ? prefix : "  ", new Style(color)));
            line.Spans.Add(new TextSpan(segments[i], new Style(color)));
            result.Add(line);
        }

        return result;
    }

    private static string Escape(string text)
        => (text ?? string.Empty)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
}
