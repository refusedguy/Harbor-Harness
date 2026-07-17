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

        // Report measured geometry back so the screen can feed it into the model
        // (UiState.TotalLines / ViewportLines) for scroll clamping + percentage.
        TotalLines = lines.Count;
        ViewportLines = maxHeight;

        // Tail-follow: ScrollOffset 0 shows the newest lines; the reducer keeps it at
        // 0 while the user is pinned to the bottom, so this always tails automatically.
        // `maxHeight <= 0` means the area is unavailable yet — show everything.
        if (maxHeight > 0 && lines.Count > maxHeight)
        {
            int skip = Math.Max(0, lines.Count - maxHeight - ScrollOffset);
            if (skip > 0)
                lines = lines.Skip(skip).ToList();
        }

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
