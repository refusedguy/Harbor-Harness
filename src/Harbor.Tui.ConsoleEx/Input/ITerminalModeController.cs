namespace Harbor.Tui.ConsoleEx.Input;

/// <summary>
/// Raw-mode ownership seam (design §5.2). The renderer (phase 1) enters raw
/// mode before starting the input pipeline and guarantees Restore in finally +
/// signal handlers. Implementations must be idempotent and crash-safe:
/// Restore after a failed Enter must be a safe no-op.
/// </summary>
public interface ITerminalModeController
{
    /// <summary>Save the original terminal state and switch to raw mode
    /// (ISIG off ⇒ Ctrl+C arrives as byte 0x03 through the parser).</summary>
    void Enter();

    /// <summary>Restore the saved terminal state. Idempotent.</summary>
    void Restore();

    bool IsRaw { get; }
}
