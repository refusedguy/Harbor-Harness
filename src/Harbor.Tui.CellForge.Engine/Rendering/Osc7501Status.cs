using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>States of the OSC 7501 Program Status Protocol (REX spec v0.4).</summary>
public enum ProgramStatusState
{
    Idle,
    Working,
    Done,
    Blocked,
    Error,
    Clear,
}

/// <summary>
/// OSC 7501 Program Status Protocol formatter. Wire form is
/// «ESC ] 7501 ; state=&lt;name&gt;[:app=&lt;id&gt;] ESC \», using string
/// string terminator. Payloads are restricted to the value character set from
/// the spec so a hostile label cannot escape the OSC envelope. Zero-width:
/// the bytes never touch the cell grid or the tracked pen column (#2219).
/// </summary>
public static class Osc7501Status
{
    private static readonly byte[] ClearBytes = Encoding.ASCII.GetBytes("\u001B]7501;state=clear\u001B\\");

    /// <summary>Feature-detection query; no reply within a timeout means unsupported.</summary>
    public const string SupportQuery = "\u001B]7501;?\u001B\\";

    public static ReadOnlySpan<byte> Clear => ClearBytes;

    public static byte[] Format(ProgramStatusState state, string? app = null)
    {
        string name = state switch
        {
            ProgramStatusState.Idle => "idle",
            ProgramStatusState.Working => "working",
            ProgramStatusState.Done => "done",
            ProgramStatusState.Blocked => "blocked",
            ProgramStatusState.Error => "error",
            ProgramStatusState.Clear => "clear",
            _ => "idle",
        };

        var sb = new StringBuilder(32);
        sb.Append("\u001B]7501;state=").Append(name);
        if (!string.IsNullOrEmpty(app))
        {
            sb.Append(":app=").Append(Sanitize(app));
        }

        sb.Append('\u001B').Append('\\');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Keeps values inside the spec's [A-Za-z0-9_.,+/=-] alphabet.</summary>
    private static string Sanitize(ReadOnlySpan<char> value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ',' or '+' or '/' or '=' or '-' ? c : '_');
        }

        return sb.ToString();
    }
}

/// <summary>
/// Per-session OSC 7501 state. The capability switch gates emission; bytes are
/// computed on <see cref="Set"/> and shipped once by <see cref="Emit"/> through
/// <see cref="AnsiWriter.RawBytes"/>, so the protocol rides the frame's single
/// backend write and never creates a side channel (#2219).
/// </summary>
public sealed class ProgramStatusEmitter
{
    private byte[]? _pending;

    /// <summary>False by default: no bytes until the probe confirms support.</summary>
    public bool Enabled { get; set; }

    public void Set(ProgramStatusState state, string? app = null)
    {
        if (Enabled)
        {
            _pending = Osc7501Status.Format(state, app);
        }
    }

    public void Clear()
    {
        if (Enabled)
        {
            _pending = Osc7501Status.Clear.ToArray();
        }
    }

    public void Emit(AnsiWriter writer)
    {
        if (_pending is { } bytes)
        {
            writer.RawBytes(bytes);
            _pending = null;
        }
    }
}
