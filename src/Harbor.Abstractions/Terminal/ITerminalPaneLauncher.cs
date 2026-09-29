namespace Harbor.Abstractions.Terminal;

/// <summary>
///     One launched, PTY-backed interactive terminal session. The handle the UI
///     renders and types into; it is deliberately opaque so that no Presentation
///     assembly can reach the process behind it.
/// </summary>
public interface ITerminalPane : IAsyncDisposable
{
    /// <summary>Process id of the child, for diagnostics and log correlation.</summary>
    int Pid { get; }

    /// <summary>Raised off the UI thread for each raw output chunk from the child.</summary>
    event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    /// <summary>Raised off the UI thread once the child's output side reached EOF.</summary>
    event EventHandler? OutputClosed;

    /// <summary>Writes one line to the child's stdin (the byte canonical shells map to Enter).</summary>
    /// <param name="line">The line to send, without a trailing newline.</param>
    void WriteLine(string line);
}

/// <summary>Raw output produced by a terminal session. UTF-8 may split across chunks.</summary>
/// <param name="data">The raw bytes produced by the child (owned copy, safe to retain).</param>
public sealed class TerminalOutputEventArgs(byte[] data) : EventArgs
{
    /// <summary>Raw bytes produced by the child.</summary>
    public byte[] Data { get; } = data;
}

/// <summary>
///     What to launch: the program, its argv, the directory it starts in and its
///     window size. Data only — a request cannot start anything by itself.
/// </summary>
/// <param name="Shell">Executable to launch (PATH-resolved when it has no separator).</param>
/// <param name="Args">Arguments passed verbatim to <paramref name="Shell" />.</param>
/// <param name="WorkingDirectory">The directory the child starts in.</param>
/// <param name="Cols">Initial terminal width.</param>
/// <param name="Rows">Initial terminal height.</param>
public sealed record TerminalPaneRequest(
    string Shell,
    IReadOnlyList<string> Args,
    string WorkingDirectory,
    int Cols = 120,
    int Rows = 32)
{
    /// <summary>
    ///     An interactive login-free shell rooted at <paramref name="workingDirectory" />.
    /// </summary>
    /// <remarks>
    ///     The <c>-i</c> argv decision lives HERE, in the contract, rather than in
    ///     every caller: it is the whole reason this request is privileged, so it
    ///     must be stated once and greppable, not re-spelled at each call site.
    /// </remarks>
    /// <param name="workingDirectory">The directory the shell starts in.</param>
    /// <param name="shellExecutable">
    ///     The shell to run. Resolving <c>$SHELL</c> is the caller's job — reading
    ///     the environment is a presentation concern, and doing it here would put
    ///     <c>Environment.GetEnvironmentVariable</c> behind a contract that has no
    ///     business knowing the user's login shell.
    /// </param>
    public static TerminalPaneRequest InteractiveShell(string workingDirectory, string shellExecutable)
        => new(shellExecutable, ["-i"], workingDirectory);
}

/// <summary>
///     Starts terminal sessions on the caller's behalf, behind the permission
///     gate. Issue #672: this is the seam a Presentation assembly talks to
///     instead of spawning a process itself.
/// </summary>
/// <remarks>
/// <para>
///     <b>Why this contract exists.</b> <c>TerminalPaneViewModel</c> used to call
///     <c>PtyProcess.Start</c> from its constructor — a real interactive shell,
///     with the full inherited environment, forked straight out of a ViewModel.
///     That put it outside the <c>ITool</c>/<c>PermissionRuleset</c> seam the rest
///     of the harness is built on, made it uncancellable and left nothing to fake
///     under test. The same defect #537 fixed for <c>GitService</c>, and strictly
///     worse: git ran three read-only arguments, this runs a shell that waits for
///     input.
/// </para>
/// <para>
///     <b>Permission gating is the point of the seam, not an afterthought.</b>
///     Unlike the git badge — read-only chrome the user opened themselves, and
///     deliberately ungated — an interactive shell is the most privileged thing
///     this harness can start. Every implementation must therefore consult a
///     <c>PermissionRuleset</c> and refuse unless the launch is permitted. The
///     contract is deliberately narrow: it exposes no way to start a session
///     without asking.
/// </para>
/// </remarks>
public interface ITerminalPaneLauncher
{
    /// <summary>
    ///     Starts a terminal session if, and only if, the active ruleset permits
    ///     it. A refusal is a <see cref="Result.Failure{TValue}" /> carrying the
    ///     reason, and guarantees no process was started.
    /// </summary>
    /// <param name="request">The program, argv, directory and window size to launch.</param>
    /// <param name="cancellationToken">Cancels the launch before the session is created.</param>
    /// <returns>The live session, or a failure explaining why it was refused.</returns>
    Result<ITerminalPane> Launch(TerminalPaneRequest request, CancellationToken cancellationToken = default);
}
