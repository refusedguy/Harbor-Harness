using System.Collections.Immutable;
using Harbor.Tui.Abstractions.State;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.View;
/// <summary>
///     Facade: wires chrome + history + layout shell. This is what the screen talks to.
///     Not a god object — orchestration only (~100 lines).
/// </summary>
internal class ChatViewProjector
{
    private readonly ChatTranscriptCache _cache = new();
    private readonly ChatChromeView _chrome = new();
    private readonly ChatHistoryView _history;
    private readonly ChatLayoutShell _shell = new();

    public ChatViewProjector()
    {
        _history = new ChatHistoryView(_cache);
    }

    // ── chrome / content inputs ────────────────────────────────────────────

    public string Status { get => _chrome.Status; set => _chrome.Status = value; }
    public string Model { get => _chrome.Model; set => _chrome.Model = value; }
    public string Provider { get => _chrome.Provider; set => _chrome.Provider = value; }
    public string Agent { get => _chrome.Agent; set => _chrome.Agent = value; }
    public bool IsReadingInput { get => _chrome.IsReadingInput; set => _chrome.IsReadingInput = value; }
    public bool IsStreaming { get => _chrome.IsStreaming; set => _chrome.IsStreaming = value; }
    public int TokensIn { get => _chrome.TokensIn; set => _chrome.TokensIn = value; }
    public int TokensOut { get => _chrome.TokensOut; set => _chrome.TokensOut = value; }
    public decimal Cost { get => _chrome.Cost; set => _chrome.Cost = value; }
    public string InputText { get => _chrome.InputText; set => _chrome.InputText = value; }
    public FocusMode Focus { get => _chrome.Focus; set => _chrome.Focus = value; }
    public string FooterText { get => _chrome.FooterText; set => _chrome.FooterText = value; }

    public string StreamBuffer
    {
        get => _history.StreamBuffer;
        set => _history.StreamBuffer = value;
    }

    public string ThinkBuffer
    {
        get => _history.ThinkBuffer;
        set => _history.ThinkBuffer = value;
    }

    public int ScrollOffset
    {
        get => _history.ScrollOffset;
        set => _history.ScrollOffset = value;
    }

    // ── measured outputs ───────────────────────────────────────────────────

    public int SourceCount => _history.SourceCount;
    public int TotalLines => _history.TotalLines;
    public int ViewportLines => _history.ViewportLines;
    public int EffectiveScroll => _history.EffectiveScroll;
    public int MaxScroll => _history.MaxScroll;
    public int HistoryTopRow => _history.HistoryTopRow;

    public Layout Layout => _shell.Layout;

    /// <summary>Backward-compatible alias for markdown flag.</summary>
    public static bool RenderMarkdownEnabled
    {
        get => ChatMarkdown.Enabled;
        set => ChatMarkdown.Enabled = value;
    }

    public void SetLines(ImmutableArray<ChatLine> lines, bool isStreaming, ActiveMessage active, int historyWidth = 0)
    {
        IsStreaming = isStreaming;
        StreamBuffer = active.TextBuffer ?? string.Empty;
        ThinkBuffer = active.ThinkBuffer ?? string.Empty;
        _cache.Sync(lines, Math.Max(0, historyWidth));
    }

    public void InvalidateHistoryCache() => _cache.Clear();

    public IReadOnlyDictionary<string, IWidget> BuildWidgets(int historyHeight)
    {
        _shell.Ensure(IsStreaming);

        var historyWidget = _history.Build(historyHeight, IsStreaming);

        var map = new Dictionary<string, IWidget>(8)
        {
            ["Header"] = _chrome.BuildHeader(),
            ["History"] = historyWidget,
            ["Input"] = _chrome.BuildInput(),
            ["Footer"] = _chrome.BuildFooter(MaxScroll, EffectiveScroll)
        };

        if (IsStreaming)
            map["StreamBar"] = _chrome.BuildStreamBar(StreamBuffer, ThinkBuffer);

        return map;
    }
}
