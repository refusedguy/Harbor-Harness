using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions.State;
using System.Collections.Immutable;
using Spectre.Console;
using Spectre.Tui;

namespace Harbor.Tui.SpectreTui.Helpers;

/// <summary>
///     Projects the shared, renderer-agnostic <see cref="UiState" /> into a Spectre.TUI
///     widget tree each frame. Pure view layer: it holds no UI logic, no scroll math,
///     no focus decisions — all of that lives in <see cref="UiReducer" />. The screen
///     copies the relevant <see cref="UiState" /> fields into these settable
///     properties, and <c>BuildWidgets</c> renders them. No <c>IAgent</c>, no effects.
/// </summary>
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

    /// <summary>Keyboard focus owner (mirrored from <see cref="UiState.Focus" />).</summary>
    public FocusMode Focus { get; set; } = FocusMode.Input;

    /// <summary>Pre-rendered footer line (assembled by the screen from the keymap).</summary>
    public string FooterText { get; set; } = string.Empty;

    /// <summary>History scroll-back offset, mirrored from <see cref="UiState.ScrollOffset" />.</summary>
    public int ScrollOffset { get; set; }

    /// <summary>Total wrapped history rows, mirrored from <see cref="UiState.TotalLines" />.</summary>
    public int TotalLines { get; set; }

    /// <summary>Visible history rows this frame, mirrored from <see cref="UiState.ViewportLines" />.</summary>
    public int ViewportLines { get; set; }

    private ImmutableArray<ChatLine> _lines = ImmutableArray<ChatLine>.Empty;

    /// <summary>
    ///     Set the transcript + live streaming content for this frame. Called once
    ///     per render from the screen's <c>SyncLayout</c>.
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
        // Build only the visible window of ChatLines. Scrolling is expressed as
        // ScrollOffset = number of wrapped rows lifted up from the tail (0 = bottom).
        // We slice the SOURCE lines before expanding them into TextLines so we never
        // allocate/style rows that are off-screen — this keeps long transcripts fast.
        var source = _lines;

        int total = source.Length;
        int top = 0;
        if (maxHeight > 0 && total > maxHeight)
        {
            top = Math.Clamp(ScrollOffset == 0
                ? total - maxHeight
                : Math.Min(ScrollOffset, total - 1), 0, total - 1);
        }

        var lines = new List<TextLine>();
        void AppendRole(ChatRole role, string content)
        {
            foreach (var block in RoleLines(role, content))
                lines.Add(block);
            lines.Add(Blank());
        }

        for (int i = top; i < total; i++)
            AppendRole(source[i].Role, source[i].Text);

        if (IsStreaming)
        {
            if (!string.IsNullOrEmpty(ThinkBuffer))
                AppendRole(ChatRole.Thinking, ThinkBuffer.Trim());
            if (!string.IsNullOrEmpty(StreamBuffer))
                AppendRole(ChatRole.Assistant, StreamBuffer.Trim());
        }

        if (lines.Count == 0)
            lines.Add(TextLine.FromMarkup("[dim]no messages yet…[/]"));

        // Report measured geometry back so the screen can feed it into the model
        // (UiState.TotalLines / ViewportLines) for scroll clamping + percentage.
        TotalLines = lines.Count;
        ViewportLines = maxHeight;

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

    // Opt-in rendering of inline markdown (bold/italic/code/headings) as styled spans.
    // OFF by default: the markdown scan runs on every visible line each frame and can
    // stall the UI during long streamed responses, so it stays behind a feature flag
    // until it is proven cheap enough to enable unconditionally.
    public static bool RenderMarkdownEnabled { get; set; }

    private static readonly System.Text.RegularExpressions.Regex HeadingRegex =
        new(@"^\s{0,3}(#{1,3})\s+(.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // The transcript is immutable, so a rendered line's text never changes. Cache the
    // parsed spans per text so we don't re-run the markdown scan on every frame (the
    // screen re-renders several times per second during streaming).
    private static readonly Dictionary<string, List<TextSpan>> MarkdownCache = new(256);

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
            line.Spans.Add(new TextSpan(i == 0 ? prefix : "  ", new Style(color)));
            // When markdown rendering is disabled the body is emitted verbatim (no
            // parsing cost); otherwise it is scanned into styled spans.
            if (RenderMarkdownEnabled)
                line.Spans.AddRange(GetMarkdownSpans(segments[i], color));
            else
                line.Spans.Add(new TextSpan(segments[i], new Style(color)));
            result.Add(line);
        }

        return result;
    }

    private static List<TextSpan> GetMarkdownSpans(string text, Color baseColor)
    {
        lock (MarkdownCache)
        {
            if (MarkdownCache.TryGetValue(text, out var cached))
                return cached;
        }

        var spans = RenderMarkdown(text, baseColor).ToList();

        lock (MarkdownCache)
        {
            if (MarkdownCache.Count > 1024)
                MarkdownCache.Clear();
            MarkdownCache[text] = spans;
        }

        return spans;
    }

    /// <summary>
    ///     Convert a single line of agent output into styled spans, interpreting the
    ///     common inline markdown tokens. The base <paramref name="baseColor" /> is used
    ///     for plain text; headings get a distinct colour.
    /// </summary>
    private static IEnumerable<TextSpan> RenderMarkdown(string text, Color baseColor)
    {
        // Heading: a line starting with 1-3 '#' followed by space.
        var heading = HeadingRegex.Match(text);
        if (heading.Success)
        {
            yield return new TextSpan(heading.Groups[2].Value, new Style(Color.Yellow, null, Decoration.Bold));
            yield break;
        }

        var result = new List<(string Text, Style Style)>();
        int i = 0;
        while (i < text.Length)
        {
            // Fenced code span: `code`
            if (text[i] == '`' && i + 1 < text.Length && text[i + 1] != '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    result.Add((text.Substring(i + 1, end - i - 1), new Style(Color.Grey)));
                    i = end + 1;
                    continue;
                }
            }

            // Bold **text** or __text__
            if ((text[i] == '*' && Peek(text, i, "**")) || (text[i] == '_' && Peek(text, i, "__")))
            {
                string token = text[i] == '*' ? "**" : "__";
                int end = text.IndexOf(token, i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    result.Add((text.Substring(i + 2, end - i - 2), new Style(baseColor, null, Decoration.Bold)));
                    i = end + 2;
                    continue;
                }
            }

            // Italic *text* or _text_
            if (text[i] == '*' || text[i] == '_')
            {
                char c = text[i];
                int end = text.IndexOf(c, i + 1);
                if (end > i && (end == text.Length - 1 || text[end + 1] != c))
                {
                    result.Add((text.Substring(i + 1, end - i - 1), new Style(baseColor, null, Decoration.Italic)));
                    i = end + 1;
                    continue;
                }
            }

            // Plain run up to the next special char.
            int next = IndexOfAny(text, i, '*', '_', '`');
            result.Add((text.Substring(i, next - i), new Style(baseColor)));
            i = next;
        }

        foreach (var (t, s) in result)
            yield return new TextSpan(t, s);
    }

    private static int IndexOfAny(string text, int start, params char[] chars)
    {
        int best = text.Length;
        foreach (var c in chars)
        {
            int idx = text.IndexOf(c, start);
            if (idx >= 0 && idx < best) best = idx;
        }
        return best;
    }

    private static bool Peek(string text, int i, string token)
        => i + token.Length <= text.Length && text.Substring(i, token.Length) == token;

    private static string Escape(string text)
        => (text ?? string.Empty)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
}
