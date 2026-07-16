using System.Collections.Generic;

namespace Harbor.Tui.SpectreTui.Components;

/// <summary>
///     Prompt input buffer with up/down history navigation, mirroring the
///     behaviour of the existing fullscreen renderer's input state.
/// </summary>
internal sealed class InputState
{
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string _draft = string.Empty;

    public string Text => _draft;

    public int Length => _draft.Length;

    public int HistoryCount => _history.Count;

    public int HistoryIndex => _historyIndex;

    public bool IsEmpty => _draft.Length == 0;

    public void Clear()
    {
        _draft = string.Empty;
        _historyIndex = -1;
    }

    public void Append(char c) => _draft += c;

    public void Backspace()
    {
        if (_draft.Length > 0)
            _draft = _draft[..^1];
    }

    public void NavigateUp()
    {
        if (_history.Count == 0) return;
        _historyIndex = _historyIndex < 0
            ? _history.Count - 1
            : Math.Max(0, _historyIndex - 1);
        _draft = _history[_historyIndex];
    }

    public void NavigateDown()
    {
        if (_history.Count == 0 || _historyIndex < 0) return;
        _historyIndex++;
        if (_historyIndex >= _history.Count)
        {
            _historyIndex = -1;
            _draft = string.Empty;
        }
        else
        {
            _draft = _history[_historyIndex];
        }
    }

    public string Consume()
    {
        var result = _draft;
        if (!string.IsNullOrWhiteSpace(result))
            _history.Add(result);
        _draft = string.Empty;
        _historyIndex = -1;
        return result;
    }
}
