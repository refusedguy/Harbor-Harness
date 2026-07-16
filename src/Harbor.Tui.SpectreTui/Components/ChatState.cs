namespace Harbor.Tui.SpectreTui.Components;

/// <summary>
///     Rolling chat history. Each entry carries a semantic role so the layout
///     builder can colour and prefix it consistently (user / assistant / tool / …).
/// </summary>
internal sealed class ChatState
{
    public sealed record ChatLine(string Role, string Content);

    private readonly List<ChatLine> _lines = new();

    public IReadOnlyList<ChatLine> Lines => _lines;

    public int Count => _lines.Count;

    public void Add(string role, string content)
        => _lines.Add(new ChatLine(role, content));

    public void Clear() => _lines.Clear();
}
