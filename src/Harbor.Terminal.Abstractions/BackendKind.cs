namespace Harbor.Terminal.Abstractions;

/// <summary>
///     Identifies the renderer backend targeted by the <see cref="TuiRendererAttribute" />
///     (RendererAdapterGenerator). Each value maps to a known output strategy.
/// </summary>
public enum BackendKind : byte
{
    /// <summary>ANSI SGR truecolor output (real terminals).</summary>
    Ansi = 0,

    /// <summary>Plain text, no escape codes (pipes, CI logs).</summary>
    Plain = 1,

    /// <summary>CellForge SGR automaton with diff-based frame emission.</summary>
    CellForge = 2,

    /// <summary>NickConsoleEx / SharpConsoleUI markup output.</summary>
    NickConsoleEx = 3,
}
