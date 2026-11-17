using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// OSC 7 working-directory report: «ESC ] 7 ; file://&lt;host&gt;&lt;path&gt; ESC \».
/// Terminals use it for "new tab here" / "reveal in editor" and session
/// restoration. The payload is a file URI, so every non-unreserved byte of the
/// path is percent-encoded and ESC/BEL cannot break out of the OSC envelope.
/// Zero-width: the report never moves the pen or changes a cell (#2219).
/// </summary>
public static class Osc7WorkingDirectory
{
    public static byte[] Format(string path, string? host = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var sb = new StringBuilder(path.Length + 16);
        sb.Append("\u001B]7;file://").Append(SanitizeHost(host)).Append(EscapePath(path)).Append('\u001B').Append('\\');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string SanitizeHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(host.Length);
        foreach (char c in host)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_');
        }

        return sb.ToString();
    }

    private static string EscapePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        byte[] bytes = Encoding.UTF8.GetBytes(normalized);
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            bool unreserved = b is >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9'
                or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' or (byte)'/';
            if (unreserved)
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
        }

        return sb.ToString();
    }
}

/// <summary>Per-session OSC 7 emitter; dedupes so a steady path emits once.</summary>
public sealed class WorkingDirectoryEmitter
{
    private byte[]? _pending;
    private string? _lastKey;

    public bool Enabled { get; set; }

    public void Set(string path, string? host = null)
    {
        if (!Enabled)
        {
            return;
        }

        string key = host + "\u0000" + path;
        if (key == _lastKey)
        {
            return;
        }

        _lastKey = key;
        _pending = Osc7WorkingDirectory.Format(path, host);
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
