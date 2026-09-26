namespace Harbor.Tui.CellForge.Rendering;

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

    /// <summary>
    /// Synchronous frame write for synchronous render contexts (the
    /// <see cref="CellForgeTuiRenderer"/> adapter path — renderer-unification
    /// sprint). Default implementation refuses; backends that serve the sync
    /// adapter must override. Frame atomicity matches <see cref="WriteAsync"/>.
    /// </summary>
    virtual void Write(ReadOnlySpan<byte> bytes) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not support synchronous writes; it cannot back CellForgeRenderContext.");
}
