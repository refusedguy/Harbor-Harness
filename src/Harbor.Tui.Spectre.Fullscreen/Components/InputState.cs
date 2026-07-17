using System.Text;
namespace Harbor.Tui.Spectre.Fullscreen.Components;
/// <summary>
///     Manages input buffer and history navigation.
///     Single responsibility: track user input state and history.
/// </summary>
public sealed class InputState
{
    private readonly StringBuilder _buffer = new();
    private readonly List<string> _history = new();

    public string Text => _buffer.ToString();
    public int Length => _buffer.Length;
    public char this[int index] => _buffer[index];
    public int HistoryCount => _history.Count;
    public int HistoryIndex
    {
        get;
        private set;
    } = -1;
    public bool IsEmpty => _buffer.Length == 0;

    public void Append(char c) => _buffer.Append(c);
    public void Backspace()
    {
        if (_buffer.Length > 0)
            _buffer.Remove(_buffer.Length - 1, 1);
    }
    public void Clear()
    {
        _buffer.Clear();
        HistoryIndex = -1;
    }

    public void Submit(string text)
    {
        _history.Add(text);
        HistoryIndex = -1;
        _buffer.Clear();
    }

    public void NavigateUp()
    {
        if (_history.Count == 0) return;
        if (HistoryIndex == -1)
            HistoryIndex = _history.Count - 1;
        else if (HistoryIndex > 0)
            HistoryIndex--;

        _buffer.Clear();
        _buffer.Append(_history[HistoryIndex]);
    }

    public void NavigateDown()
    {
        if (HistoryIndex < 0) return;

        if (HistoryIndex < _history.Count - 1)
        {
            HistoryIndex++;
            _buffer.Clear();
            _buffer.Append(_history[HistoryIndex]);
        }
        else
        {
            HistoryIndex = -1;
            _buffer.Clear();
        }
    }

    public string Consume()
    {
        string result = _buffer.ToString();
        _buffer.Clear();
        HistoryIndex = -1;
        return result;
    }
}
