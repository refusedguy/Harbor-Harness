namespace Harbor.Tui.Spectre.Fullscreen.Components;

/// <summary>
/// Manages chat line state with bounded memory.
/// Single responsibility: store and trim chat history.
/// </summary>
public sealed class ChatState
{
    private const int MaxLines = 500;
    private readonly List<ChatLine> _lines = new();

    public IReadOnlyList<ChatLine> Lines => _lines;
    public int Count => _lines.Count;

    public void Add(string role, string content)
    {
        _lines.Add(new ChatLine(role, content));
        Trim();
    }

    public void Clear() => _lines.Clear();

    private void Trim()
    {
        while (_lines.Count > MaxLines)
            _lines.RemoveAt(0);
    }

    public sealed record ChatLine(string Role, string Content);
}
