// SC10 (#1218, epic #1195): test input seam + fixed geometry.
// Ports the key-queue idea from Spectre's Testing/{TestConsoleInput,
// TestCapabilities}.cs (queued keys, fixed 80x24 profile) onto cells: keys go
// through ConsoleKeyInfo (the DialogOverlay legacy-path vocabulary), asserts
// read ScreenBuffer cells via GridDump.Art. Deliberately NOT Spectre's
// segment-based TestConsole.Write — snapshots/goldens/PTY stay where they are
// and are not duplicated here.

namespace Harbor.Tui.CellForge.Tests;

/// <summary>Queued key input for form unit tests (Spectre TestConsoleInput shape).</summary>
internal sealed class TestInputQueue
{
    private readonly Queue<ConsoleKeyInfo> _keys = new();

    public int Count => _keys.Count;

    public bool IsEmpty => _keys.Count == 0;

    /// <summary>Enqueues printable text, one key per char (\r/\n become Enter).</summary>
    public void PushText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char c in text)
        {
            PushKey(c is '\r' or '\n'
                ? new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)
                : new ConsoleKeyInfo(c, ConsoleKey.A, false, false, false));
        }
    }

    public void PushKey(ConsoleKeyInfo key) => _keys.Enqueue(key);

    public void PushKey(ConsoleKey key, char ch = '\0') =>
        _keys.Enqueue(new ConsoleKeyInfo(ch, key, false, false, false));

    /// <exception cref="InvalidOperationException">When the queue is empty.</exception>
    public ConsoleKeyInfo ReadKey() =>
        _keys.TryDequeue(out var key) ? key : throw new InvalidOperationException("TestInputQueue is empty.");

    public void DrainTo(Action<ConsoleKeyInfo> handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        while (_keys.TryDequeue(out var key))
        {
            handle(key);
        }
    }
}

/// <summary>Fixed 80x24 screen for form unit tests + text asserts over cells.</summary>
internal static class TestScreen
{
    public const int Cols = 80;

    public const int Rows = 24;

    public static ScreenBuffer NewBuffer() => new(Cols, Rows);

    public static Rect Viewport() => new(0, 0, Cols, Rows);

    /// <summary>One rtrimmed text line per row (wide tails already collapsed by Art).</summary>
    public static string[] Lines(ScreenBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        string[] split = TestRender.Art(buffer).Split('\n');
        var lines = new string[buffer.Rows];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = (i < split.Length ? split[i] : string.Empty).TrimEnd();
        }

        return lines;
    }

    /// <summary>Full text with normalized \n endings (Spectre TestConsole.Output shape).</summary>
    public static string Output(ScreenBuffer buffer) => string.Join("\n", Lines(buffer));
}
