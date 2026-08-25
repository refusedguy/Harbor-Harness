namespace Harbor.Tui.ConsoleEx.Rendering;

/// <summary>
/// Write seam for terminal frames (celldiff §3.1): every frame is assembled
/// in one buffer and leaves the process through a single
/// <see cref="WriteAsync"/> call. Production wiring uses <see cref="StdoutBackend"/>;
/// tests capture frames with an in-memory backend.
/// </summary>
public interface ITerminalBackend
{
    /// <summary>Writes one atomic byte span (a whole frame) to the tty.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
}
