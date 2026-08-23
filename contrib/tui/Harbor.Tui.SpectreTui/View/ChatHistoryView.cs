using Harbor.Ui.Framework.State;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.View;
/// <summary>
///     Display-row scroll window over committed cache + optional pinned stream.
///     ScrollOffset = rows lifted from BOTTOM (0 = live tail).
/// </summary>
internal sealed class ChatHistoryView
{
    private readonly ChatTranscriptCache _cache;
    private readonly List<TextLine> _streamRows = new(64);

    public ChatHistoryView(ChatTranscriptCache cache)
    {
        _cache = cache;
    }

    public int ScrollOffset { get; set; }
    public string StreamBuffer { get; set; } = string.Empty;
    public string ThinkBuffer { get; set; } = string.Empty;

    public int TotalLines { get; private set; }
    public int ViewportLines { get; private set; }
    public int EffectiveScroll { get; private set; }
    public int MaxScroll { get; private set; }
    public int HistoryTopRow { get; private set; }
    public int SourceCount => _cache.SourceCount;

    public IWidget Build(int viewportRows, bool isStreaming)
    {
        viewportRows = Math.Max(0, viewportRows);
        ViewportLines = viewportRows;

        bool pinned = ScrollOffset <= 0;

        _streamRows.Clear();
        if (isStreaming && pinned)
        {
            if (!string.IsNullOrWhiteSpace(ThinkBuffer))
            {
                ChatMessageFormatter.AppendRole(
                    _streamRows, ChatRole.Thinking, ThinkBuffer.Trim(), false);
            }

            if (!string.IsNullOrWhiteSpace(StreamBuffer))
            {
                ChatMessageFormatter.AppendRole(
                    _streamRows, ChatRole.Assistant, StreamBuffer.Trim(), false);
            }
        }

        int committed = _cache.Rows.Count;
        int stream = _streamRows.Count;
        int total = committed + stream;
        TotalLines = total;

        if (total == 0 || viewportRows <= 0)
        {
            MaxScroll = 0;
            EffectiveScroll = 0;
            HistoryTopRow = 0;
            ScrollOffset = 0;
            return EmptyHistory();
        }

        MaxScroll = Math.Max(0, total - viewportRows);
        EffectiveScroll = Math.Clamp(ScrollOffset, 0, MaxScroll);
        ScrollOffset = EffectiveScroll;

        int top = Math.Max(0, total - viewportRows - EffectiveScroll);
        int end = Math.Min(total, top + viewportRows);
        HistoryTopRow = top;

        var paragraph = new Paragraph().Alignment(Justify.Left);
        var rows = _cache.Rows;

        int cEnd = Math.Min(end, committed);
        for (int i = Math.Min(top, committed); i < cEnd; i++)
            paragraph.Lines.Add(rows[i]);

        if (stream > 0 && end > committed)
        {
            int s0 = Math.Max(0, top - committed);
            int s1 = Math.Min(stream, end - committed);
            for (int i = s0; i < s1; i++)
                paragraph.Lines.Add(_streamRows[i]);
        }

        if (paragraph.Lines.Count == 0)
            paragraph.Lines.Add(TextLine.FromMarkup("[dim]  no messages yet[/]"));

        return paragraph;
    }

    private static IWidget EmptyHistory()
    {
        var empty = new Paragraph().Alignment(Justify.Left);
        empty.Lines.Add(TextLine.FromMarkup(""));
        empty.Lines.Add(TextLine.FromMarkup("[dim]  no messages yet[/]"));
        empty.Lines.Add(TextLine.FromMarkup("[dim]  type below · /help for commands[/]"));
        return empty;
    }
}
