using System.Text;

namespace Harbor.Tui.Spectre.Fullscreen.Components;

/// <summary>
/// Manages scroll offset state for the chat history view.
/// Single responsibility: track and compute scroll position.
/// </summary>
public sealed class ScrollManager
{
    private int _offset;
    private bool _isScrolling;

    public int Offset => _offset;
    public bool IsScrolling => _isScrolling;

    public void ScrollUp(int lines, int totalLines, int viewportHeight)
    {
        var maxScroll = Math.Max(0, totalLines - viewportHeight);
        _offset = Math.Min(_offset + lines, maxScroll);
        _isScrolling = _offset > 0;
    }

    public void ScrollDown(int lines)
    {
        _offset = Math.Max(0, _offset - lines);
        if (_offset == 0) _isScrolling = false;
    }

    public void ScrollToTop(int totalLines, int viewportHeight)
    {
        _offset = Math.Max(0, totalLines - viewportHeight);
        _isScrolling = _offset > 0;
    }

    public void ScrollToBottom()
    {
        _offset = 0;
        _isScrolling = false;
    }

    public void Reset()
    {
        _offset = 0;
        _isScrolling = false;
    }
}
