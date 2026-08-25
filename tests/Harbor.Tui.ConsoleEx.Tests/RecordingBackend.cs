using System.Text;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>In-memory <see cref="ITerminalBackend"/> capturing every frame write.</summary>
internal sealed class RecordingBackend : ITerminalBackend
{
    private readonly List<byte[]> _writes = [];

    public IReadOnlyList<byte[]> Writes => _writes;

    /// <summary>All captured bytes decoded as UTF-8 (escape chars kept raw).</summary>
    public string Text
    {
        get
        {
            var combined = new List<byte>();
            foreach (var w in _writes)
            {
                combined.AddRange(w);
            }

            return Encoding.UTF8.GetString([.. combined]);
        }
    }

    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (var w in _writes)
            {
                total += w.Length;
            }

            return total;
        }
    }

    public void ResetForTests() => _writes.Clear();

    /// <summary>Control characters rendered visible (\e, \r, \n) so TUnit
    /// comparisons never see raw CR/LF.</summary>
    public string Escaped => Text
        .Replace("\u001B", "\\e")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n");

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        _writes.Add(bytes.ToArray());
        return ValueTask.CompletedTask;
    }
}
