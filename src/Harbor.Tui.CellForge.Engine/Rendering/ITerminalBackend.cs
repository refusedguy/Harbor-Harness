namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Write seam for terminal frames (celldiff §3.1): every frame is assembled
/// in one buffer and leaves the process through a single
/// <see cref="WriteAsync"/> call. Production wiring uses <see cref="StdoutBackend"/>;
/// tests capture frames with an in-memory backend.
///
/// <para>
/// This is the ASYNC-ONLY contract. Anything that must ship a frame from a
/// synchronous call site (the <c>ITuiRenderContext</c> adapter,
/// <c>ScreenSession.FlushFrame</c>) requires <see cref="ISyncTerminalBackend"/>
/// instead — issue #468. Keeping the two apart means an async-only backend no
/// longer compiles when handed to a sync sink, instead of throwing out of a
/// default interface method at paint time.
/// </para>
/// </summary>
public interface ITerminalBackend
{
    /// <summary>Writes one atomic byte span (a whole frame) to the tty.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
}
