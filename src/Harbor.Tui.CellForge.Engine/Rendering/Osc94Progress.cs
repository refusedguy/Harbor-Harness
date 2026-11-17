using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>OSC 9;4 progress states (ConEmu/iTerm2/Ghostty/Windows Terminal).</summary>
public enum ProgressState
{
    None = 0,          // remove the indicator
    Normal = 1,        // normal, percentage in 0..100
    Error = 2,         // error, percentage in 0..100
    Indeterminate = 3, // busy, no percentage
    Paused = 4,        // paused, percentage in 0..100
}

/// <summary>
/// OSC 9;4 progress formatter: «ESC ] 9 ; 4 ; &lt;state&gt; [; &lt;value&gt;] ESC \».
/// The value is clamped to 0..100 and omitted for the indeterminate/clear
/// states. Zero-width, so a long operation's progress never changes the grid
/// (#2219).
/// </summary>
public static class Osc94Progress
{
    public static byte[] Format(ProgressState state, int value)
    {
        int code = (int)state;
        string payload = code is (int)ProgressState.None or (int)ProgressState.Indeterminate
            ? $"\u001B]9;4;{code}\u001B\\"
            : $"\u001B]9;4;{code};{Math.Clamp(value, 0, 100)}\u001B\\";
        return Encoding.ASCII.GetBytes(payload);
    }
}

/// <summary>Per-session progress emitter; see <see cref="ProgramStatusEmitter"/>.</summary>
public sealed class ProgressEmitter
{
    private byte[]? _pending;
    private ProgressState? _last;
    private int _lastValue;

    public bool Enabled { get; set; }

    public void Set(ProgressState state, int value = 0)
    {
        if (!Enabled || (_last == state && _lastValue == value && state != ProgressState.Normal))
        {
            return;
        }

        _last = state;
        _lastValue = value;
        _pending = Osc94Progress.Format(state, value);
    }

    public void Clear() => Set(ProgressState.None, 0);

    public void Emit(AnsiWriter writer)
    {
        if (_pending is { } bytes)
        {
            writer.RawBytes(bytes);
            _pending = null;
        }
    }
}
