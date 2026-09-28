namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// An <see cref="ITerminalBackend"/> that can also ship a frame from a
/// SYNCHRONOUS call site (issue #468).
///
/// <para>
/// The synchronous write has no default implementation on purpose. It used to
/// live on <see cref="ITerminalBackend"/> as a <c>virtual</c> member that threw
/// <see cref="NotSupportedException"/>, so a backend that implemented only
/// <see cref="ITerminalBackend.WriteAsync"/> compiled cleanly and then blew up
/// inside <c>CellForgeRenderContext.Flush</c> / <c>ScreenSession.FlushFrame</c>.
/// Splitting the contract moves the failure to compile time for every sink that
/// can only work synchronously.
/// </para>
///
/// <para>
/// Implement it when the frame must leave the process without an async
/// state machine — the <c>ITuiRenderContext</c> adapter contract is entirely
/// synchronous, and the alt-screen render loop cannot yield per frame.
/// </para>
/// </summary>
public interface ISyncTerminalBackend : ITerminalBackend
{
    /// <summary>
    /// Synchronous frame write for synchronous render contexts. Frame
    /// atomicity matches <see cref="ITerminalBackend.WriteAsync"/>: the span is
    /// one whole frame and must reach the tty in one shot.
    /// </summary>
    void Write(ReadOnlySpan<byte> bytes);
}
