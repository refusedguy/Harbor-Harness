namespace Harbor.Tools.Builtin;

/// <summary>
///     One line of child-process output carried by <see cref="IProcessHandle" />'s
///     stream events (#1590). <see cref="Line" /> is <c>null</c> at end-of-stream —
///     the sentinel BCL's <c>DataReceivedEventArgs.Data</c> uses.
/// </summary>
public sealed class ProcessLineEventArgs : EventArgs
{
    /// <summary>Instantiates one line event; <paramref name="line" /> is <c>null</c> for end-of-stream.</summary>
    public ProcessLineEventArgs(string? line) => Line = line;

    /// <summary>The line as delivered by the stream (without the trailing newline).</summary>
    public string? Line { get; }
}
