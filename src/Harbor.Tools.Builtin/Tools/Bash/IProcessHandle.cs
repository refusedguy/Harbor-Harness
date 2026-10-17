namespace Harbor.Tools.Builtin;

/// <summary>
///     The tool-side handle of one running child process (#1590). Mirrors the
///     six things BashTool does with `System.Diagnostics.Process` — stream
///     events, begin/cancel reading, wait, kill the tree, poll exit state —
///     without leaking the concrete type.
/// </summary>
/// <remarks>
///     Line events use <c>null</c> as the end-of-stream sentinel, exactly like
///     BCL's <c>DataReceivedEventArgs.Data</c>. Implementations must only emit
///     after <see cref="BeginReading" /> (BCL buffers until <c>BeginOutputReadLine</c>),
///     so a subscriber attached right after <see cref="IProcessProxy.Start" />
///     never misses data.
/// </remarks>
public interface IProcessHandle : IDisposable
{
    /// <summary>Raised for each stdout line, then once with <c>null</c> at end-of-stream.</summary>
    event Action<string?> StdoutLine;

    /// <summary>Raised for each stderr line, then once with <c>null</c> at end-of-stream.</summary>
    event Action<string?> StderrLine;

    /// <summary>Starts the asynchronous line readers (BCL: BeginOutputReadLine + BeginErrorReadLine).</summary>
    void BeginReading();

    /// <summary>Cancels the asynchronous line readers; late events may still drain EOF sentinels.</summary>
    void CancelReading();

    /// <summary>Completes when the child exits; throws <c>OperationCanceledException</c> when <paramref name="cancellationToken" /> fires first.</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Whether the child has exited.</summary>
    bool HasExited { get; }

    /// <summary>The child's exit code (only meaningful once <see cref="HasExited" />).</summary>
    int ExitCode { get; }

    /// <summary>Kills the whole process tree, swallowing already-gone races (§ROP-A П.14 — shared helper).</summary>
    void KillTree();
}
