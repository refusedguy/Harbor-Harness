namespace Harbor.Tui.RendererTests.Support;

using System.Text;
using SharpConsoleUI;
using SharpConsoleUI.Core;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using Point = System.Drawing.Point;
using Size = SharpConsoleUI.Helpers.Size;

/// <summary>
///     IConsoleDriver decorator that records the composed cell grid while
///     forwarding everything to a wrapped <see cref="HeadlessConsoleDriver"/>
///     (renderer-unification sprint Phase 5 capture seam).
/// </summary>
/// <remarks>
///     SharpConsoleUI's headless driver intentionally suppresses console
///     output (its ConsoleBuffer sets SuppressConsoleOutput), so golden-frame
///     capture hooks the driver-level cell writes instead: SetNarrowCell /
///     FillCells / WriteBufferRegion build the exact visible screen, which is
///     a more stable golden artifact than raw ANSI bytes (no SGR churn, no
///     cursor repositioning noise).
/// </remarks>
public sealed class RecordingConsoleDriver : IConsoleDriver
{
    private readonly HeadlessConsoleDriver _inner;
    private readonly string?[,] _grid;

    public RecordingConsoleDriver(int width, int height)
    {
        _inner = new HeadlessConsoleDriver(width, height);
        _grid = new string?[width, height];
    }

    public Size ScreenSize => _inner.ScreenSize;

    /// <summary>
    ///     The composed visible screen: one string per row, trailing blanks
    ///     trimmed, trailing empty rows dropped.
    /// </summary>
    public string Snapshot()
    {
        int width = _inner.ScreenSize.Width;
        int height = _inner.ScreenSize.Height;
        var rows = new List<string>(height);
        for (int y = 0; y < height; y++)
        {
            var sb = new StringBuilder(width);
            for (int x = 0; x < width; x++)
            {
                sb.Append(_grid[x, y] ?? " ");
            }

            rows.Add(sb.ToString().TrimEnd());
        }

        while (rows.Count > 0 && rows[^1].Length == 0)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return string.Join("\n", rows);
    }

    public void Clear()
    {
        Array.Clear(_grid);
        _inner.Clear();
    }

    public void Flush() => _inner.Flush();

    public void Start() => _inner.Start();

    public void Stop() => _inner.Stop();

    public void SetCursorPosition(int x, int y) => _inner.SetCursorPosition(x, y);

    public void SetCursorVisible(bool visible) => _inner.SetCursorVisible(visible);

    public void SetCursorShape(CursorShape shape) => _inner.SetCursorShape(shape);

    public void ResetCursorShape() => _inner.ResetCursorShape();

    public void Initialize(ConsoleWindowSystem windowSystem) => _inner.Initialize(windowSystem);

    public void SetNarrowCell(int x, int y, char character, Color fg, Color bg)
    {
        if (InBounds(x, y))
        {
            _grid[x, y] = character.ToString();
        }

        _inner.SetNarrowCell(x, y, character, fg, bg);
    }

    public void FillCells(int x, int y, int width, char character, Color fg, Color bg)
    {
        for (int i = 0; i < width; i++)
        {
            if (InBounds(x + i, y))
            {
                _grid[x + i, y] = character.ToString();
            }
        }

        _inner.FillCells(x, y, width, character, fg, bg);
    }

    public void WriteBufferRegion(int destX, int destY, CharacterBuffer source, int srcX, int srcY, int width, Color fallbackBg)
    {
        for (int i = 0; i < width; i++)
        {
            if (InBounds(destX + i, destY))
            {
                _grid[destX + i, destY] = source.GetCell(srcX + i, srcY).Character.ToString();
            }
        }

        _inner.WriteBufferRegion(destX, destY, source, srcX, srcY, width, fallbackBg);
    }

    public int GetDirtyCharacterCount() => _inner.GetDirtyCharacterCount();

    // Event plumbing: the window system subscribes on the instance it is
    // given (this decorator). Headless input simulation is not exercised by
    // golden-frame tests, so no event is forwarded to the inner driver; the
    // raisers below exist so future input tests can drive them directly.
    public event EventHandler<ConsoleKeyInfo>? KeyPressed;
    public event EventHandler<string>? Paste;
    public event IConsoleDriver.MouseEventHandler? MouseEvent;
    public event EventHandler<Size>? ScreenResized;

    public void SimulateKey(ConsoleKeyInfo key) => KeyPressed?.Invoke(this, key);

    public void SimulatePaste(string text) => Paste?.Invoke(this, text);

    public void SimulateResize(Size newSize) => ScreenResized?.Invoke(this, newSize);

    public void SimulateMouse(List<MouseFlags> flags, Point point) => MouseEvent?.Invoke(this, flags, point);

    private bool InBounds(int x, int y) =>
        x >= 0 && y >= 0 && x < _grid.GetLength(0) && y < _grid.GetLength(1);
}
