using System.Collections.Immutable;
using Harbor.Tui.Abstractions.State;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.View;
internal sealed class ChatTranscriptCache
{
    private readonly List<TextLine> _rows = new(256);
    private ImmutableArray<ChatLine> _source = ImmutableArray<ChatLine>.Empty;

    public IReadOnlyList<TextLine> Rows => _rows;

    public int SourceCount => _source.IsDefault ? 0 : _source.Length;

    public void Sync(ImmutableArray<ChatLine> lines)
    {
        if (lines == _source)
            return;

        if (lines.IsDefaultOrEmpty)
        {
            _rows.Clear();
            _source = lines.IsDefault ? ImmutableArray<ChatLine>.Empty : lines;
            return;
        }

        if (_source.Length > 0 && lines.Length >= _source.Length)
        {
            bool prefixOk = true;
            for (int i = 0; i < _source.Length; i++)
            {
                if (!lines[i].Equals(_source[i]))
                {
                    prefixOk = false;
                    break;
                }
            }

            if (prefixOk)
            {
                for (int i = _source.Length; i < lines.Length; i++)
                    ChatMessageFormatter.AppendRole(
                        _rows, lines[i].Role, lines[i].Text, markdown: ChatMarkdown.Enabled);

                _source = lines;
                return;
            }
        }

        _rows.Clear();
        for (int i = 0; i < lines.Length; i++)
            ChatMessageFormatter.AppendRole(
                _rows, lines[i].Role, lines[i].Text, markdown: ChatMarkdown.Enabled);

        _source = lines;
    }

    public void Clear()
    {
        _rows.Clear();
        _source = ImmutableArray<ChatLine>.Empty;
    }
}