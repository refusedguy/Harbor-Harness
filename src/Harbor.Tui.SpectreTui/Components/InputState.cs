namespace Harbor.Tui.SpectreTui.Components;
/// <summary>
///     Prompt input buffer with up/down history navigation, mirroring the
///     behaviour of the existing fullscreen renderer's input state.
/// </summary>
internal sealed class InputState
{
    private readonly List<string> _history = new();

    public string Text
    {
        get;
        private set;
    } = string.Empty;

    public int Length => Text.Length;

    public int HistoryCount => _history.Count;

    public int HistoryIndex
    {
        get;
        private set;
    } = -1;

    public bool IsEmpty => Text.Length == 0;

    public void Clear()
    {
        Text = string.Empty;
        HistoryIndex = -1;
    }

    public void Append(char c) => Text += c;

    public void Backspace()
    {
        if (Text.Length > 0)
            Text = Text[..^1];
    }

    public void NavigateUp()
    {
        if (_history.Count == 0) return;
        HistoryIndex = HistoryIndex < 0
            ? _history.Count - 1
            : Math.Max(0, HistoryIndex - 1);
        Text = _history[HistoryIndex];
    }

    public void NavigateDown()
    {
        if (_history.Count == 0 || HistoryIndex < 0) return;
        HistoryIndex++;
        if (HistoryIndex >= _history.Count)
        {
            HistoryIndex = -1;
            Text = string.Empty;
        }
        else
        {
            Text = _history[HistoryIndex];
        }
    }

    public string Consume()
    {
        string result = Text;
        if (!string.IsNullOrWhiteSpace(result))
            _history.Add(result);
        Text = string.Empty;
        HistoryIndex = -1;
        return result;
    }
}
