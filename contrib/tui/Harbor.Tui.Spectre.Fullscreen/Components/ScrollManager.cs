namespace Harbor.Tui.Spectre.Fullscreen.Components;
/// <summary>
///     Manages scroll offset state for the chat history view.
///     Single responsibility: track and compute scroll position.
/// </summary>
public sealed class ScrollManager
{

    public int Offset
    {
        get;
        private set;
    }
    public bool IsScrolling
    {
        get;
        private set;
    }

    public void ScrollUp(int lines, int totalLines, int viewportHeight)
    {
        int maxScroll = Math.Max(0, totalLines - viewportHeight);
        Offset = Math.Min(Offset + lines, maxScroll);
        IsScrolling = Offset > 0;
    }

    public void ScrollDown(int lines)
    {
        Offset = Math.Max(0, Offset - lines);
        if (Offset == 0) IsScrolling = false;
    }

    public void ScrollToTop(int totalLines, int viewportHeight)
    {
        Offset = Math.Max(0, totalLines - viewportHeight);
        IsScrolling = Offset > 0;
    }

    public void ScrollToBottom()
    {
        Offset = 0;
        IsScrolling = false;
    }

    public void Reset()
    {
        Offset = 0;
        IsScrolling = false;
    }
}
