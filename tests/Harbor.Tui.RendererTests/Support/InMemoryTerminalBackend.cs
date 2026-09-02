namespace Harbor.Tui.RendererTests.Support;

using System.Text;
using Harbor.Tui.CellForge.Rendering;

/// <summary>
///     In-memory <see cref="ITerminalBackend"/> capturing every frame the
///     CellForge pipeline emits — the golden-frame capture seam the
///     <c>ITerminalBackend</c> doc comment anticipated ("tests capture frames
///     with an in-memory backend").
/// </summary>
public sealed class InMemoryTerminalBackend : ITerminalBackend
{
    private readonly StringBuilder _buffer = new();

    /// <summary>Everything written so far, as a single string.</summary>
    public string Output => _buffer.ToString();

    public void Reset() => _buffer.Clear();

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        Append(bytes.Span);
        return ValueTask.CompletedTask;
    }

    public void Write(ReadOnlySpan<byte> bytes) => Append(bytes);

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        _buffer.Append(Encoding.UTF8.GetString(bytes.ToArray()));
    }
}
