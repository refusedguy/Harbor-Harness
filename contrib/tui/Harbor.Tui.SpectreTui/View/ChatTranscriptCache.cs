using System.Collections.Immutable;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.View;
/// <summary>
///     Append-only-ish cache of committed <see cref="ChatLine" /> → display
///     <see cref="TextLine" /> rows. New lines are appended; when the source
///     prefix is unchanged the tail is added incrementally. A change in render
///     width forces a full rebuild because tables are width-dependent.
/// </summary>
internal sealed class ChatTranscriptCache
{
    private readonly List<TextLine> _rows = new(256);
    private ImmutableArray<ChatLine> _source = ImmutableArray<ChatLine>.Empty;
    private int _width = -1;

    public IReadOnlyList<TextLine> Rows => _rows;

    public int SourceCount => _source.IsDefault ? 0 : _source.Length;

    public void Sync(ImmutableArray<ChatLine> lines, int width)
    {
        if (lines == _source && width == _width)
            return;

        if (lines.IsDefaultOrEmpty)
        {
            _rows.Clear();
            _source = lines.IsDefault ? ImmutableArray<ChatLine>.Empty : lines;
            _width = width;
            return;
        }

        // Width change (resize) re-expands everything: tables depend on width.
        if (width != _width)
        {
            _rows.Clear();
            _source = ImmutableArray<ChatLine>.Empty;
            _width = width;
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
                {
                    ChatMessageFormatter.AppendRole(
                        _rows, lines[i].Role, lines[i].Text, ChatMarkdown.Enabled, _width);
                }

                _source = lines;
                return;
            }
        }

        _rows.Clear();
        for (int i = 0; i < lines.Length; i++)
        {
            ChatMessageFormatter.AppendRole(
                _rows, lines[i].Role, lines[i].Text, ChatMarkdown.Enabled, _width);
        }

        _source = lines;
        _width = width;
    }

    public void Clear()
    {
        _rows.Clear();
        _source = ImmutableArray<ChatLine>.Empty;
    }
}
