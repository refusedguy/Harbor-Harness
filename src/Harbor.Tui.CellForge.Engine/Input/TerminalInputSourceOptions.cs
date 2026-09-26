namespace Harbor.Tui.CellForge.Input;

/// <summary>Knobs for the terminal input pipeline. Timeouts map to design
/// §2.4 (ESC flush ≈50 ms, env-tunable later) and §4.2 (paste watchdog 10 s).</summary>
public sealed class TerminalInputSourceOptions
{
    /// <summary>Lone-ESC flush delay at chunk boundaries. Zero disables.</summary>
    public TimeSpan EscFlushTimeout { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Hard abort for unclosed bracketed pastes. Zero disables.</summary>
    public TimeSpan PasteAbortTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Resize polling interval; null disables resize detection
    /// (SIGWINCH-based detection replaces it in phase 2).</summary>
    public TimeSpan? ResizePollInterval { get; init; }

    /// <summary>Viewport size probe for resize polling. Null disables.</summary>
    public Func<(int Width, int Height)>? SizeProvider { get; init; }

    public int ReadBufferSize { get; init; } = 4096;

    public static TerminalInputSourceOptions Default { get; } = new();
}
