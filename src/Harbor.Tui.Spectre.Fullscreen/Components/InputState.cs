using System.Text;

namespace Harbor.Tui.Spectre.Fullscreen.Components;

/// <summary>
/// Manages input buffer and history navigation.
/// Single responsibility: track user input state and history.
/// </summary>
public sealed class InputState
{
    private readonly StringBuilder _buffer = new();
    private readonly List<string> _history = new();
    private int _historyIndex = -1;

    public string Text => _buffer.ToString();
    public int Length => _buffer.Length;
    public char this[int index] => _buffer[index];
    public int HistoryCount => _history.Count;
    public int HistoryIndex => _historyIndex;

    public void Append(char c) => _buffer.Append(c);
    public void Backspace()
    {
        if (_buffer.Length > 0)
            _buffer.Remove(_buffer.Length - 1, 1);
    }
    public void Clear()
    {
        _buffer.Clear();
        _historyIndex = -1;
    }
    public bool IsEmpty => _buffer.Length == 0;

    public void Submit(string text)
    {
        _history.Add(text);
        _historyIndex = -1;
        _buffer.Clear();
    }

    public void NavigateUp()
    {
        if (_history.Count == 0) return;
        if (_historyIndex == -1)
            _historyIndex = _history.Count - 1;
        else if (_historyIndex > 0)
            _historyIndex--;

        _buffer.Clear();
        _buffer.Append(_history[_historyIndex]);
    }

    public void NavigateDown()
    {
        if (_historyIndex < 0) return;

        if (_historyIndex < _history.Count - 1)
        {
            _historyIndex++;
            _buffer.Clear();
            _buffer.Append(_history[_historyIndex]);
        }
        else
        {
            _historyIndex = -1;
            _buffer.Clear();
        }
    }

    public string Consume()
    {
        var result = _buffer.ToString();
        _buffer.Clear();
        _historyIndex = -1;
        return result;
    }
}
