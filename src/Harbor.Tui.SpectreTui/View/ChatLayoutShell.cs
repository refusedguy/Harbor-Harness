using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.View;
/// <summary>
///     Spectre row tree only. Toggles a 1-row StreamBar while streaming.
/// </summary>
internal sealed class ChatLayoutShell
{
    private bool _streaming;

    public Layout Layout { get; private set; } = Create(streaming: false);

    public void Ensure(bool streaming)
    {
        if (_streaming == streaming)
            return;
        _streaming = streaming;
        Layout = Create(streaming);
    }

    private static Layout Create(bool streaming)
    {
        if (streaming)
        {
            return new Layout("Root").SplitRows(
                new Layout("Header").Size(1),
                new Layout("History"),
                new Layout("StreamBar").Size(1),
                new Layout("Input").Size(3),
                new Layout("Footer").Size(1));
        }

        return new Layout("Root").SplitRows(
            new Layout("Header").Size(1),
            new Layout("History"),
            new Layout("Input").Size(3),
            new Layout("Footer").Size(1));
    }
}