namespace Harbor.Tools.Builtin;

/// <summary>
///     Proxy seam for process creation and lifecycle (#1590): the only place
///     BashTool reaches an OS process. The production implementation
///     (<see cref="SystemProcessProxy" />) drives `System.Diagnostics.Process`;
///     tests substitute a scripted fake, which is why BashTool's behaviour
///     tests no longer require a live <c>/bin/bash</c>.
/// </summary>
/// <remarks>
///     Deliberately narrow: start (or fail), then manage the returned
///     <see cref="IProcessHandle" /> until it is disposed. Kill, drain and
///     wait semantics belong to the implementations — a *restricting* proxy
///     can later deny or debounce them without touching the tool.
/// </remarks>
public interface IProcessProxy
{
    /// <summary>
    ///     Create (but not begin reading) the child process described by
    ///     <paramref name="spec" />. Returns a failure instead of throwing:
    ///     "start failed" is an expected outcome, not an exception.
    /// </summary>
    /// <remarks>Output events must not fire before <see cref="IProcessHandle.BeginReading" />.</remarks>
    Result<IProcessHandle> Start(ProcessSpec spec);
}
