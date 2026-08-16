using System.IO;
using System;

namespace Harbor.E2E.Framework;
/// <summary>
///     Common contract for driving a Harbor app (CLI, TUI, or desktop) from
///     outside the process. Each driver wraps a real subprocess (or, for
///     Avalonia, an in-process headless instance) and exposes the same surface
///     so test code is renderer-agnostic.
/// </summary>
/// <remarks>
///     <para>
///         <b>Lifecycle:</b>
///         <list type="number">
///             <item><see cref="StartAsync" /> — spawn the app with args + env.</item>
///             <item><see cref="SendInputAsync" /> / <see cref="SendKeyAsync" /> — drive it.</item>
///             <item><see cref="ReadScreenAsync" /> / <see cref="WaitForTextAsync" /> — observe.</item>
///             <item><see cref="WaitForExitAsync" /> (graceful) OR <see cref="StopAsync" /> (forceful).</item>
///         </list>
///     </para>
///     <para>
///         All methods are async and accept an optional <see cref="CancellationToken" />.
///         Implementations MUST honour the token — long-running waits (PTY reads,
///         process exit polls) must cancel promptly.
///     </para>
///     <para>
///         <b>Thread safety:</b> implementations MUST be safe for a single
///         producer (test thread sending input) + single consumer (test thread
///         reading screen). Concurrent sends from multiple threads are NOT
///         required.
///     </para>
/// </remarks>
public interface IE2eDriver : IAsyncDisposable
{
    /// <summary>
    ///     Whether the wrapped app is still running. <see langword="true" /> from
    ///     a successful <see cref="StartAsync" /> until <see cref="WaitForExitAsync" />
    ///     returns or <see cref="StopAsync" /> is called.
    /// </summary>
    public bool IsRunning { get; }

    /// <summary>
    ///     Start the wrapped app with the given args + environment variables.
    ///     Replaces the test process's environment with <paramref name="env" />
    ///     (plus the inherited ones for keys not in the dictionary).
    /// </summary>
    /// <param name="args">Command-line args forwarded to the app entry point.</param>
    /// <param name="env">
    ///     Env vars applied to the spawned process. <see langword="null" /> = inherit
    ///     the test process's environment unchanged.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public Task StartAsync(string[] args, IDictionary<string, string>? env = null, CancellationToken ct = default);

    /// <summary>
    ///     Send raw input (a string) to the app's stdin. For TUI renderers this
    ///     typically types the characters into the input field; for the CLI
    ///     REPL it types into the prompt.
    /// </summary>
    /// <remarks>
    ///     No newline is appended — call <see cref="SendKeyAsync" /> with
    ///     <see cref="ConsoleKey.Enter" /> to submit.
    /// </remarks>
    public Task SendInputAsync(string input, CancellationToken ct = default);

    /// <summary>
    ///     Send a single keystroke. For TUI renderers this is the only way to
    ///     send special keys (Enter, Escape, F-keys, Ctrl-combos).
    /// </summary>
    /// <param name="key">The logical key (independent of OS).</param>
    /// <param name="modifiers">Modifier flags (Ctrl/Shift/Alt).</param>
    /// <param name="ct">Cancellation token.</param>
    public Task SendKeyAsync(ConsoleKey key, ConsoleModifiers modifiers = ConsoleModifiers.None, CancellationToken ct = default);

    /// <summary>
    ///     Read the current rendered screen as plain text. For TUI renderers,
    ///     this is the visible terminal content (ANSI escape sequences stripped).
    ///     For the CLI one-shot driver, this is the captured stdout.
    /// </summary>
    /// <returns>The current screen text (may be a snapshot of a rolling buffer).</returns>
    public Task<string> ReadScreenAsync(CancellationToken ct = default);

    /// <summary>
    ///     Poll <see cref="ReadScreenAsync" /> until <paramref name="pattern" />
    ///     appears in the captured text, or the timeout elapses.
    /// </summary>
    /// <param name="pattern">Substring to search for (case-sensitive).</param>
    /// <param name="timeout">Wait cap. Defaults to 10 seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true" /> if the pattern was seen in time.</returns>
    public Task<bool> WaitForTextAsync(string pattern, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>
    ///     Block until the wrapped process exits, or the timeout elapses.
    /// </summary>
    /// <param name="timeout">Wait cap. Defaults to 30 seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The process exit code, or <c>-1</c> if the timeout elapsed.</returns>
    public Task<int> WaitForExitAsync(TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>
    ///     Forcefully terminate the wrapped process if still running. Idempotent.
    ///     No-op if the process already exited.
    /// </summary>
    public Task StopAsync(CancellationToken ct = default);
}

/// <summary>
///     Helper utilities for E2E tests.
/// </summary>
public static class E2EHelpers
{
    /// <summary>
    ///     Walk up from current directory until we find <c>Harbor.sln</c>.
    ///     Used to locate the repository root for screenshot directories.
    /// </summary>
    public static string FindRepoRoot()
    {
        // Try multiple starting points
        string[] startDirs = 
        [
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        ];

        foreach (string startDir in startDirs)
        {
            string dir = startDir;
            while (dir is not null && !File.Exists(Path.Combine(dir, "Harbor.sln")))
            {
                DirectoryInfo? parent = Directory.GetParent(dir);
                dir = parent?.FullName;
            }
            
            if (dir is not null) return dir;
        }
        
        // Last resort: return current directory even if .sln not found
        return Directory.GetCurrentDirectory();
    }
}
